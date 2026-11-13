using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Safety;

public enum NoticeSeverity { Info, Caution, Blocking }

public sealed record Notice(NoticeSeverity Severity, string Title, string Detail, int? PartitionNumber = null);

public enum PlannedAction { ShrinkVolume, ExtendVolume, CreateVolume }

/// <summary>
/// Whether an action could be considered in a future version, and why not if it can't.
/// In this version every action is unavailable: the app only inspects. This exists so users
/// can already see *why* something would be blocked, and so the rules are tested early.
/// </summary>
public sealed record ActionAssessment(PlannedAction Action, bool PassesBasicChecks, IReadOnlyList<string> Reasons);

public static class SafetyAnalyzer
{
    public const string ReadOnlyStatement =
        "This version only reads information. It does not change, format, resize or write to any disk.";

    private static readonly string[] ResizableFileSystems = ["NTFS", "REFS"];

    public static IReadOnlyList<Notice> AnalyzeDisk(DiskLayout layout)
    {
        var d = layout.Disk;
        var n = new List<Notice>();

        foreach (var problem in layout.Problems)
            n.Add(new Notice(NoticeSeverity.Blocking, "Layout could not be verified", problem));

        if (d.IsDynamic)
            n.Add(new Notice(NoticeSeverity.Blocking, "Dynamic disk",
                "This is a dynamic disk. Dynamic disks are deprecated by Microsoft and are not supported by ShadowDisk. All change actions will stay disabled."));
        if (d.Style == PartitionStyle.Raw)
            n.Add(new Notice(NoticeSeverity.Caution, "Disk not initialized",
                "Windows reports no partition table. Initializing a disk erases whatever is on it, so ShadowDisk will never do this without a separate, explicit request."));
        if (d.Style == PartitionStyle.Unknown)
            n.Add(new Notice(NoticeSeverity.Blocking, "Unknown partition style", "Windows did not report a recognised partition style."));
        if (d.IsOffline)
            n.Add(new Notice(NoticeSeverity.Blocking, "Disk is offline", "Windows has this disk offline. Its contents cannot be inspected fully."));
        if (d.IsReadOnly)
            n.Add(new Notice(NoticeSeverity.Info, "Disk is read-only", "Windows reports this disk as read-only."));
        if (d.IsSystem || d.IsBoot)
            n.Add(new Notice(NoticeSeverity.Caution, "Windows runs from this disk",
                "This disk holds the running Windows installation or its boot files. Mistakes here can stop the PC from starting."));
        if (d.IsRemovableBus)
            n.Add(new Notice(NoticeSeverity.Caution, "Removable or USB disk",
                "Removable drives can be unplugged mid-operation and many USB enclosures report incomplete health information."));
        if (d.Style == PartitionStyle.Mbr && d.SizeBytes > 2 * ByteSize.TiB)
            n.Add(new Notice(NoticeSeverity.Caution, "MBR disk larger than 2 TiB",
                "MBR can only address the first 2 TiB of a disk with 512-byte sectors. Space beyond that may be unusable."));

        switch (d.Health)
        {
            case ReportedHealth.Warning:
                n.Add(new Notice(NoticeSeverity.Caution, "Windows reports a health warning",
                    "Back up this disk before doing anything else with it."));
                break;
            case ReportedHealth.Unhealthy:
                n.Add(new Notice(NoticeSeverity.Blocking, "Windows reports this disk as unhealthy",
                    "Copy important data off this disk now. Changes to a failing disk can make data loss more likely."));
                break;
        }

        foreach (var region in layout.Regions.Where(r => r.Partition is not null))
            n.AddRange(AnalyzePartition(region.Partition!));

        return n;
    }

    public static IEnumerable<Notice> AnalyzePartition(Partition p)
    {
        if (p.IsSystem && p.Kind != PartitionKind.EfiSystem)
            yield return new Notice(NoticeSeverity.Blocking, "System partition",
                "Holds the files your PC needs to start (on MBR disks this is usually “System Reserved”). It is protected and will never be offered for changes.", p.PartitionNumber);
        if (p.IsBoot)
            yield return new Notice(NoticeSeverity.Caution, "Windows is installed here",
                "This volume holds the running Windows installation. Any future change to it needs a full backup first.", p.PartitionNumber);

        switch (p.Kind)
        {
            case PartitionKind.EfiSystem:
                yield return new Notice(NoticeSeverity.Blocking, "EFI System Partition",
                    "Holds the files your PC needs to start. It is protected and will never be offered for changes.", p.PartitionNumber);
                break;
            case PartitionKind.MicrosoftReserved:
                yield return new Notice(NoticeSeverity.Blocking, "Microsoft Reserved Partition",
                    "Reserved by Windows. It is protected and will never be offered for changes.", p.PartitionNumber);
                break;
            case PartitionKind.Recovery:
                yield return new Notice(NoticeSeverity.Caution, "Recovery partition",
                    "Used by Windows to repair or reset the PC. Moving or deleting it can break recovery and updates.", p.PartitionNumber);
                break;
            case PartitionKind.OemOrHidden:
                yield return new Notice(NoticeSeverity.Caution, "Unrecognised partition type",
                    "This may be a vendor, Linux or other operating system partition. ShadowDisk will not offer changes to it.", p.PartitionNumber);
                break;
        }

        if (p.Volume is { } v)
        {
            switch (v.Encryption)
            {
                case EncryptionState.Encrypted:
                    yield return new Notice(NoticeSeverity.Caution, "BitLocker encrypted",
                        "Make sure you have the BitLocker recovery key saved somewhere other than this PC before any disk change.", p.PartitionNumber);
                    break;
                case EncryptionState.Locked:
                    yield return new Notice(NoticeSeverity.Blocking, "Locked encrypted volume",
                        "This volume is locked. Its contents and free space cannot be read until it is unlocked.", p.PartitionNumber);
                    break;
            }
            if (v.FileSystem is null or "RAW")
                yield return new Notice(NoticeSeverity.Caution, "File system not recognised",
                    "Windows cannot read the file system on this volume. Used and free space are not known.", p.PartitionNumber);
            if (v.Health == ReportedHealth.Unhealthy)
                yield return new Notice(NoticeSeverity.Blocking, "Windows reports this volume as unhealthy",
                    "Windows suggests running a file system check. Back up first.", p.PartitionNumber);
        }
    }

    public static IReadOnlyList<ActionAssessment> AssessPartition(DiskLayout layout, Partition p)
    {
        var common = CommonBlockers(layout.Disk);
        var result = new List<ActionAssessment>();

        var shrink = new List<string>(common);
        AddVolumeBlockers(shrink, p);
        result.Add(new ActionAssessment(PlannedAction.ShrinkVolume, shrink.Count == 0, shrink));

        var extend = new List<string>(common);
        AddVolumeBlockers(extend, p);
        var regions = layout.Regions.ToList();
        var index = regions.FindIndex(r => r.Partition == p);
        var next = index >= 0 ? regions.Skip(index + 1).FirstOrDefault(r => r.Kind != RegionKind.Gap) : null;
        if (next is not { Kind: RegionKind.Unallocated })
            extend.Add("Windows can only extend a volume into unallocated space directly after it on the same disk. There is none.");
        result.Add(new ActionAssessment(PlannedAction.ExtendVolume, extend.Count == 0, extend));

        return result;
    }

    public static ActionAssessment AssessUnallocated(DiskLayout layout, DiskRegion region)
    {
        var reasons = new List<string>(CommonBlockers(layout.Disk));
        if (region.Kind != RegionKind.Unallocated)
            reasons.Add("This is not unallocated space.");
        if (layout.Disk.Style == PartitionStyle.Mbr)
        {
            var primaries = layout.Disk.Partitions.Count(p => p.Kind != PartitionKind.Logical);
            if (primaries >= 4)
                reasons.Add("MBR disks hold at most four primary partitions and this disk already has four.");
        }
        return new ActionAssessment(PlannedAction.CreateVolume, reasons.Count == 0, reasons);
    }

    private static List<string> CommonBlockers(PhysicalDisk d)
    {
        var r = new List<string>();
        if (d.IsDynamic) r.Add("Dynamic disks are not supported.");
        if (d.IsOffline) r.Add("The disk is offline.");
        if (d.IsReadOnly) r.Add("The disk is read-only.");
        if (d.Style is PartitionStyle.Raw or PartitionStyle.Unknown) r.Add("The disk has no recognised partition table.");
        if (d.Health == ReportedHealth.Unhealthy) r.Add("Windows reports the disk as unhealthy. Back up instead of changing it.");
        return r;
    }

    private static void AddVolumeBlockers(List<string> reasons, Partition p)
    {
        if (p.IsStartupPartition)
            reasons.Add("This partition holds the files your PC needs to start. It is protected.");
        else if (p.Kind is not (PartitionKind.Basic or PartitionKind.Logical))
            reasons.Add("Only ordinary data partitions are considered for resizing.");
        if (p.Volume is not { } v)
        {
            reasons.Add("Windows reports no readable volume on this partition.");
            return;
        }
        if (v.FileSystem is null || !ResizableFileSystems.Contains(v.FileSystem))
            reasons.Add($"Windows can only resize NTFS and ReFS volumes in place (this one is {v.FileSystem ?? "unknown"}).");
        if (v.Encryption == EncryptionState.Locked)
            reasons.Add("The volume is locked by BitLocker.");
        if (v.Health == ReportedHealth.Unhealthy)
            reasons.Add("Windows reports the volume as unhealthy.");
    }
}
