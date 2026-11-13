using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;

namespace ShadowDisk.Windows;

/// <summary>
/// Real disk changes through the Windows Storage Management API.
///
/// Before writing, this class checks AGAIN — independently of OperationRunner — that the live disk
/// is a ShadowDisk practice disk with the expected identifier. Each Windows call is made once.
/// Create volume = CreatePartition → Format (NTFS) → AddAccessPath (drive letter).
/// If a later step fails, earlier steps are NOT undone; the result says exactly what happened.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskOperations(OperationPolicy policy) : IDiskOperations
{
    public Task<CreateVolumeBackendResult> CreateVolumeAsync(PhysicalDisk disk, long offsetBytes, long sizeBytes, string volumeLabel) =>
        Task.Run(() => CreateVolume(disk, offsetBytes, sizeBytes, volumeLabel));

    private CreateVolumeBackendResult CreateVolume(PhysicalDisk disk, long offsetBytes, long sizeBytes, string volumeLabel)
    {
        if (!Elevation.IsElevated())
            return new(false, false, null, "Administrator rights are needed. Use “Restart as administrator”.");
        if (!policy.IsPracticeDisk(disk))
            return new(false, false, null, "Refused: this is not a ShadowDisk practice disk.");

        var scope = Wmi.StorageScope();
        using var live = Wmi.Disk(scope, disk.Number);
        if (live is null)
            return new(false, false, null, "The disk is no longer present.");
        var liveLocation = (live["Location"] as string)?.Trim();
        var liveId = (live["UniqueId"] as string)?.Trim();
        if (Wmi.ToUShort(live["BusType"]) != 15
            || !string.Equals(liveLocation, disk.Location, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(liveId, disk.UniqueId, StringComparison.Ordinal))
            return new(false, false, null, "Refused: the live disk does not match the practice disk that was checked.");

        // Step 1: create the partition (no drive letter yet, so Explorer doesn't pop up a "format?" prompt).
        var (createCode, createOut) = Wmi.Invoke(live, "CreatePartition", p =>
        {
            p["Size"] = (ulong)sizeBytes;
            p["Offset"] = (ulong)offsetBytes;
            p["AssignDriveLetter"] = false;
            if (disk.Style == PartitionStyle.Gpt) p["GptType"] = StorageMapper.GptBasicData;
            else p["MbrType"] = (ushort)7;
        });
        if (createCode != 0)
            return new(false, false, null, "Creating the partition failed: " + Wmi.Explain(createCode));

        var partitionNumber = createOut?["CreatedPartition"] is ManagementBaseObject created
            ? Wmi.ToInt(created["PartitionNumber"])
            : FindPartitionAt(scope, disk.Number, offsetBytes);
        if (partitionNumber < 0)
            return new(true, false, null, "The partition was created but Windows did not report its number.");

        // Step 2: format it as NTFS (quick format). Wait briefly for Windows to give it a volume path.
        var volumePath = WaitForVolumePath(scope, disk.Number, partitionNumber);
        if (volumePath is null)
            return new(true, false, null, "The partition was created but Windows did not create a volume for it to format.");

        using (var volume = Wmi.All(scope, "MSFT_Volume").FirstOrDefault(v =>
                   string.Equals(v["Path"] as string, volumePath, StringComparison.OrdinalIgnoreCase)))
        {
            if (volume is null)
                return new(true, false, null, "The partition was created but its volume could not be found to format.");
            var (formatCode, _) = Wmi.Invoke(volume, "Format", p =>
            {
                p["FileSystem"] = "NTFS";
                p["FileSystemLabel"] = volumeLabel;
                p["Full"] = false;
            });
            if (formatCode != 0)
                return new(true, false, null, "The partition was created but formatting failed: " + Wmi.Explain(formatCode));
        }

        // Step 3: give it a drive letter.
        using var partition = Wmi.Partition(scope, disk.Number, partitionNumber);
        if (partition is null)
            return new(true, true, null, "The volume was created and formatted, but the partition could not be found to give it a drive letter.");
        var (letterCode, _) = Wmi.Invoke(partition, "AddAccessPath", p => p["AssignDriveLetter"] = true);
        if (letterCode != 0)
            return new(true, true, null, "The volume was created and formatted, but Windows could not give it a drive letter: " + Wmi.Explain(letterCode));

        using var refreshed = Wmi.Partition(scope, disk.Number, partitionNumber);
        var letter = refreshed?["DriveLetter"] is char c && char.IsLetter(c) ? char.ToUpper(c, CultureInfo.InvariantCulture).ToString() : null;
        return new(true, true, letter, null);
    }

    private static int FindPartitionAt(ManagementScope scope, int diskNumber, long offset) =>
        Wmi.All(scope, "MSFT_Partition")
            .Where(p => Wmi.ToInt(p["DiskNumber"]) == diskNumber && Convert.ToInt64(p["Offset"], CultureInfo.InvariantCulture) == offset)
            .Select(p => Wmi.ToInt(p["PartitionNumber"]))
            .DefaultIfEmpty(-1)
            .First();

    private static string? WaitForVolumePath(ManagementScope scope, int diskNumber, int partitionNumber)
    {
        for (var i = 0; i < 20; i++)
        {
            using var p = Wmi.Partition(scope, diskNumber, partitionNumber);
            var path = (p?["AccessPaths"] as string[])?.FirstOrDefault(a => a.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
            if (path is not null) return path;
            Thread.Sleep(500);
        }
        return null;
    }
}
