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

    /// <summary>
    /// Creates a Recovery partition the way Microsoft documents it (KB5028997), with diskpart:
    /// "create partition primary … id=27" (MBR) or "id=de94bba4-…" (GPT), "format quick fs=ntfs label=…",
    /// then "set id=…" again (format resets the MBR type) and, on GPT, "gpt attributes=0x8000000000000001". The disk is selected by its file for practice disks, and by number
    /// for real disks only after its identifier was checked again. No drive letter is given.
    /// </summary>
    public async Task<BackendResult> CreateRecoveryPartitionAsync(PhysicalDisk disk, long offsetBytes, long sizeBytes)
    {
        if (!Elevation.IsElevated()) return new(false, "Administrator rights are needed.");
        if (offsetBytes % (1024 * 1024) != 0 || sizeBytes % (1024 * 1024) != 0)
            return new(false, "Refused: the Recovery partition must start and end on a whole MiB.");
        var refusal = await Task.Run(() => CheckLiveDisk(disk, null, out _));
        if (refusal is not null) return new(false, refusal);

        var select = policy.IsPracticeDisk(disk)
            ? $"select vdisk file=\"{disk.Location}\""
            : $"select disk {disk.Number.ToString(CultureInfo.InvariantCulture)}";
        var sizeMiB = (sizeBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        var offsetKiB = (offsetBytes / 1024).ToString(CultureInfo.InvariantCulture);
        // Formatting resets an MBR partition's type to 07 (NTFS), so the Recovery type is set again AFTER the
        // format, as in Microsoft's MBR/BIOS recovery-partition sample. The runner then reads the disk back and
        // only reports success if Windows lists a Recovery partition at exactly this place and size.
        var id = disk.Style == PartitionStyle.Gpt ? StorageMapper.GptRecovery.Trim('{', '}') : "27";
        var commands = new List<string>
        {
            select,
            $"create partition primary size={sizeMiB} offset={offsetKiB} id={id}",
            "format quick fs=ntfs label=\"Windows RE tools\"",
            $"set id={id} override",
        };
        if (disk.Style == PartitionStyle.Gpt) commands.Add("gpt attributes=0x8000000000000001");
        try
        {
            await DiskPart.RunAsync([.. commands]);
            await Task.Run(Wmi.RefreshStorageCache);   // so the read-back sees what diskpart did
            return new(true, null);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, ex.Message);
        }
    }

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
        var refusal = CheckLiveDisk(disk, partitionNumber, out var scope, extendTo: targetSizeBytes);
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
    /// The one exception is <paramref name="extendTo"/>: the Windows partition itself (boot, not system) may be
    /// made BIGGER. Shrinking, deleting or relettering it is always refused here.
    /// Returns a refusal reason, or null when it is safe to continue.
    /// </summary>
    private string? CheckLiveDisk(PhysicalDisk disk, int? partitionNumber, out ManagementScope? scope, long? extendTo = null)
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
            var isBoot = Wmi.ToBool(part["IsBoot"]);
            var isSystem = Wmi.ToBool(part["IsSystem"]);
            var growingWindows = isBoot && !isSystem && extendTo is { } size
                                 && size > Convert.ToInt64(part["Size"], CultureInfo.InvariantCulture);
            if ((isBoot || isSystem) && !growingWindows)
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
        int partitionNumber;
        var existing = Wmi.All(scope, "MSFT_Partition").Count(p => Wmi.ToInt(p["DiskNumber"]) == disk.Number);
        if (disk.Style == PartitionStyle.Mbr && existing == 3)
        {
            // The Storage API refuses a FOURTH primary partition on an MBR disk ("not supported"; Windows' own
            // tools would switch to an extended partition). diskpart creates it as a primary partition, exactly
            // as asked. Formatting and the drive letter are still done through the Storage API below.
            var select = policy.IsPracticeDisk(disk)
                ? $"select vdisk file=\"{disk.Location}\""
                : $"select disk {disk.Number.ToString(CultureInfo.InvariantCulture)}";
            try
            {
                DiskPart.RunAsync(select,
                    $"create partition primary size={(sizeBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} " +
                    $"offset={(offsetBytes / 1024).ToString(CultureInfo.InvariantCulture)} id=07").GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
            {
                return new(false, false, null, "Creating the partition failed: " + ex.Message);
            }
            Wmi.RefreshStorageCache();
            partitionNumber = FindPartitionAt(scope, disk.Number, offsetBytes);
        }
        else
        {
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
            partitionNumber = createOut?["CreatedPartition"] is ManagementBaseObject created
                ? Wmi.ToInt(created["PartitionNumber"])
                : FindPartitionAt(scope, disk.Number, offsetBytes);
        }
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
        if (partition["DriveLetter"] is char had && char.IsLetter(had))
            return new(true, true, char.ToUpper(had, CultureInfo.InvariantCulture).ToString(), null);   // Windows already gave it one
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
