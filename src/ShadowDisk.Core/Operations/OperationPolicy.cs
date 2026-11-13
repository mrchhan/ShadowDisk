using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Operations;

/// <summary>
/// Decides which disks real operations may touch. In this version that is ONLY ShadowDisk practice
/// disks: virtual disk files ShadowDisk created in its own practice folder. Physical disks stay
/// read-only until each operation has been proven on practice disks.
/// </summary>
public sealed class OperationPolicy(string practiceFolder)
{
    public const string PracticeFileExtension = ".vhd";

    public string PracticeFolder { get; } = Normalize(practiceFolder);

    public bool IsPracticeDisk(PhysicalDisk disk) =>
        disk.Bus is BusType.FileBackedVirtual or BusType.Virtual
        && disk.Location is { Length: > 0 } location
        && Normalize(location).StartsWith(PracticeFolder, StringComparison.OrdinalIgnoreCase)
        && location.EndsWith(PracticeFileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reasons the disk may not be changed. Empty means real operations are allowed.</summary>
    public IReadOnlyList<string> WhyNotAllowed(PhysicalDisk disk)
    {
        var reasons = new List<string>();
        if (!IsPracticeDisk(disk))
            reasons.Add("Real changes are only enabled on ShadowDisk practice disks in this version. " +
                        "Your physical disks stay read-only until each operation has been proven on practice disks.");
        if (disk.IsSystem || disk.IsBoot)
            reasons.Add("Windows runs from this disk.");
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
