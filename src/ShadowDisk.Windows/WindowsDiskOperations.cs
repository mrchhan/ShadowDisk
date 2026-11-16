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

    public Task<BackendResult> DeletePartitionAsync(PhysicalDisk disk, int partitionNumber, long offsetBytes) =>
        Task.Run(() => Delete(disk, partitionNumber, offsetBytes));

    public Task<BackendResult> SetDriveLetterAsync(PhysicalDisk disk, int partitionNumber, long offsetBytes, string? oldLetter, string newLetter) =>
        Task.Run(() => SetLetter(disk, partitionNumber, offsetBytes, oldLetter, newLetter));

    /// <summary>One call to MSFT_Partition.DeleteObject (the API behind Remove-Partition).</summary>
    private BackendResult Delete(PhysicalDisk disk, int partitionNumber, long offsetBytes)
    {
        if (!Elevation.IsElevated()) return new(false, "Administrator rights are needed.");
        var refusal = CheckLiveDisk(disk, partitionNumber, out var scope);
        if (refusal is not null) return new(false, refusal);

        using var partition = Wmi.Partition(scope!, disk.Number, partitionNumber);
        if (partition is null) return new(false, "The partition is no longer present.");
        if (Convert.ToInt64(partition["Offset"], CultureInfo.InvariantCulture) != offsetBytes)
            return new(false, "Refused: the partition is not at the expected position on the disk.");
        // Independent of the runner: never delete a partition Windows marks as boot or system, on ANY disk.
        if (Wmi.ToBool(partition["IsBoot"]) || Wmi.ToBool(partition["IsSystem"]))
            return new(false, "Refused: Windows reports this partition as its boot or system partition.");

        var (code, _) = Wmi.Invoke(partition, "DeleteObject");
        return code == 0 ? new(true, null) : new(false, "Deleting failed: " + Wmi.Explain(code));
    }

    /// <summary>MSFT_Partition.RemoveAccessPath (old letter) then AddAccessPath (new letter). Files are not touched.</summary>
    private BackendResult SetLetter(PhysicalDisk disk, int partitionNumber, long offsetBytes, string? oldLetter, string newLetter)
    {
        if (!Elevation.IsElevated()) return new(false, "Administrator rights are needed.");
        var refusal = CheckLiveDisk(disk, partitionNumber, out var scope);
        if (refusal is not null) return new(false, refusal);

        using var partition = Wmi.Partition(scope!, disk.Number, partitionNumber);
        if (partition is null) return new(false, "The partition is no longer present.");
        if (Convert.ToInt64(partition["Offset"], CultureInfo.InvariantCulture) != offsetBytes)
            return new(false, "Refused: the partition is not at the expected position on the disk.");
        if (Wmi.ToBool(partition["IsBoot"]) || Wmi.ToBool(partition["IsSystem"]))
            return new(false, "Refused: this is Windows' boot or system partition.");
        if (DriveInfo.GetDrives().Any(d => string.Equals(d.Name.TrimEnd('\\', ':'), newLetter, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"{newLetter}: is already in use.");

        if (oldLetter is { Length: > 0 })
        {
            var (removeCode, _) = Wmi.Invoke(partition, "RemoveAccessPath", p => p["AccessPath"] = $@"{oldLetter}:\");
            if (removeCode != 0) return new(false, "Removing the old letter failed: " + Wmi.Explain(removeCode));
        }
        var (addCode, _) = Wmi.Invoke(partition, "AddAccessPath", p => p["AccessPath"] = $@"{newLetter}:\");
        return addCode == 0 ? new(true, null) : new(false, "Assigning the new letter failed: " + Wmi.Explain(addCode));
    }

    public Task<ResizeBackendResult> ResizePartitionAsync(PhysicalDisk disk, int partitionNumber, long offsetBytes, long targetSizeBytes) =>
        Task.Run(() => Resize(disk, partitionNumber, offsetBytes, targetSizeBytes));

    /// <summary>
    /// One call to MSFT_Partition.Resize, which resizes the partition and its NTFS/ReFS file system
    /// together (the API behind Resize-Partition). Shrinking may move files inside the volume first.
    /// </summary>
    private ResizeBackendResult Resize(PhysicalDisk disk, int partitionNumber, long offsetBytes, long targetSizeBytes)
    {
        if (!Elevation.IsElevated())
            return new(false, "Administrator rights are needed. Use “Restart as administrator”.");
        var refusal = CheckLiveDisk(disk, partitionNumber, out var scope);
        if (refusal is not null) return new(false, refusal);

        using var partition = Wmi.Partition(scope!, disk.Number, partitionNumber);
        if (partition is null)
            return new(false, "The partition is no longer present.");
        if (Convert.ToInt64(partition["Offset"], CultureInfo.InvariantCulture) != offsetBytes)
            return new(false, "Refused: the partition is not at the expected position on the disk.");

        var (code, _) = Wmi.Invoke(partition, "Resize", p => p["Size"] = (ulong)targetSizeBytes);
        return code == 0 ? new(true, null) : new(false, "Resizing failed: " + Wmi.Explain(code));
    }

    /// <summary>
    /// Independent check, made right before the first Windows call, that the live disk is the one that
    /// was checked: same number and identifier; for a practice disk also the same file; for a real disk,
    /// real-disk changes must be on and the target partition must not be the Windows or start-up partition.
    /// Returns a refusal reason, or null when it is safe to continue.
    /// </summary>
    private string? CheckLiveDisk(PhysicalDisk disk, int? partitionNumber, out ManagementScope? scope)
    {
        scope = null;
        var practice = policy.IsPracticeDisk(disk);
        if (!practice && !policy.AllowRealDisks)
            return "Refused: real-disk changes are switched off, and this is not a ShadowDisk practice disk.";

        scope = Wmi.StorageScope();
        using var live = Wmi.Disk(scope, disk.Number);
        if (live is null)
            return "The disk is no longer present.";
        if (!string.Equals((live["UniqueId"] as string)?.Trim(), disk.UniqueId, StringComparison.Ordinal))
            return "Refused: the live disk does not have the identifier of the disk that was checked.";
        if (practice && (Wmi.ToUShort(live["BusType"]) != 15
                         || !string.Equals((live["Location"] as string)?.Trim(), disk.Location, StringComparison.OrdinalIgnoreCase)))
            return "Refused: the live disk does not match the practice disk that was checked.";

        if (!practice && partitionNumber is { } number)
        {
            using var part = Wmi.Partition(scope, disk.Number, number);
            if (part is null)
                return "The partition is no longer present.";
            if (Wmi.ToBool(part["IsBoot"]) || Wmi.ToBool(part["IsSystem"]))
                return "Refused: Windows reports this partition as its boot or system partition.";
        }
        return null;
    }

    private CreateVolumeBackendResult CreateVolume(PhysicalDisk disk, long offsetBytes, long sizeBytes, string volumeLabel)
    {
        if (!Elevation.IsElevated())
            return new(false, false, null, "Administrator rights are needed. Use “Restart as administrator”.");
        var refusal = CheckLiveDisk(disk, null, out var checkedScope);
        if (refusal is not null) return new(false, false, null, refusal);
        var scope = checkedScope!;
        using var live = Wmi.Disk(scope, disk.Number);
        if (live is null)
            return new(false, false, null, "The disk is no longer present.");

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
                if (!string.IsNullOrEmpty(volumeLabel)) p["FileSystemLabel"] = volumeLabel;
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
