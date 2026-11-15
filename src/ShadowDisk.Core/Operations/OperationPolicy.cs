using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Operations;

/// <summary>
/// Decides which disks and partitions real operations may touch.
///
/// • ShadowDisk practice disks (virtual disk files in the practice folder): always allowed.
/// • Real disks: only when the user has switched on "real disk changes" for this session, and then
///   only for data volumes. The partition Windows runs from, its start-up partition, EFI, MSR and
///   recovery partitions stay protected. Extra checks apply: mains power, volume health and known
///   BitLocker status.
/// </summary>
public sealed class OperationPolicy(string practiceFolder)
{
    public const string PracticeFileExtension = ".vhd";

    public string PracticeFolder { get; } = Normalize(practiceFolder);

    /// <summary>Session-only switch. Never saved, so every new start of ShadowDisk begins with it off.</summary>
    public bool AllowRealDisks { get; set; }

    public bool IsPracticeDisk(PhysicalDisk disk) =>
        disk.Bus is BusType.FileBackedVirtual or BusType.Virtual
        && disk.Location is { Length: > 0 } location
        && Normalize(location).StartsWith(PracticeFolder, StringComparison.OrdinalIgnoreCase)
        && location.EndsWith(PracticeFileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reasons the change may not be made. Empty means it is allowed.
    /// <paramref name="target"/> is the partition being resized, or null when creating a volume in free space.
    /// <paramref name="onMainsPower"/> is true/false when known, null when Windows can't tell.
    /// </summary>
    public IReadOnlyList<string> WhyNotAllowed(PhysicalDisk disk, Partition? target = null, bool? onMainsPower = null)
    {
        var reasons = new List<string>();
        var practice = IsPracticeDisk(disk);

        if (practice)
        {
            if (disk.IsSystem || disk.IsBoot) reasons.Add("Windows runs from this disk.");
        }
        else if (!AllowRealDisks)
        {
            reasons.Add("Real disks are read-only. Changes are only enabled on ShadowDisk practice disks, " +
                        "unless you switch on “Real disk changes” for this session.");
        }
        else
        {
            if (target is not null && (target.IsBoot || target.IsSystem || target.IsStartupPartition))
                reasons.Add($"{target.DisplayName} holds Windows or the files your PC needs to start. Changes to it are not enabled in this version.");
            if (target?.Volume is { } v)
            {
                if (v.Health != ReportedHealth.Healthy)
                    reasons.Add($"Windows does not report {target.DisplayName} as healthy. Run a disk check (chkdsk) and back up first.");
                if (v.Encryption == EncryptionState.Unknown)
                    reasons.Add("BitLocker status is unknown for this volume. Use “Restart as administrator” so it can be checked.");
                if (v.Encryption == EncryptionState.Locked)
                    reasons.Add("The volume is locked by BitLocker. Unlock it first.");
            }
            if (disk.Health != ReportedHealth.Healthy && disk.Health != ReportedHealth.Unknown)
                reasons.Add("Windows reports a health problem with this disk. Back up instead of changing it.");
            if (onMainsPower == false)
                reasons.Add("The PC is running on battery. Plug it in before changing a real disk: a power cut during a change can cause data loss.");
        }

        if (disk.IsOffline) reasons.Add("The disk is offline.");
        if (disk.IsReadOnly) reasons.Add("The disk is read-only.");
        if (string.IsNullOrWhiteSpace(disk.UniqueId)) reasons.Add("Windows did not report an identifier for this disk, so ShadowDisk can't be sure it is the same disk.");
        return reasons;
    }

    private static string Normalize(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        return p.EndsWith('\\') ? p : p + "\\";
    }
}

/// <summary>A fingerprint of a disk's layout. If it changes between planning and applying, the operation is refused.</summary>
public static class LayoutFingerprint
{
    public static string Of(PhysicalDisk disk) =>
        $"{disk.UniqueId}|{disk.SizeBytes}|{disk.Style}|" +
        string.Join(";", disk.Partitions.OrderBy(p => p.OffsetBytes).Select(p => $"{p.PartitionNumber}@{p.OffsetBytes}+{p.SizeBytes}"));
}
