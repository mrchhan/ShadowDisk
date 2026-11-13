using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Layout;

public enum RegionKind { Partition, Unallocated, Gap }

/// <summary>
/// One contiguous byte range of a physical disk, in on-disk order.
/// Unallocated = space not inside any partition that is large enough to be usable (≥ 1 MiB).
/// Gap = small slack (alignment, partition-table metadata) that cannot hold a partition.
/// </summary>
public sealed record DiskRegion(RegionKind Kind, long OffsetBytes, long SizeBytes, Partition? Partition)
{
    public long EndBytes => OffsetBytes + SizeBytes;
}

public sealed record DiskLayout(
    PhysicalDisk Disk,
    IReadOnlyList<DiskRegion> Regions,
    IReadOnlyList<string> Problems)
{
    public bool IsConsistent => Problems.Count == 0;
    public long AllocatedBytes => Regions.Where(r => r.Kind == RegionKind.Partition).Sum(r => r.SizeBytes);
    public long UnallocatedBytes => Regions.Where(r => r.Kind == RegionKind.Unallocated).Sum(r => r.SizeBytes);
    public long UsedDataBytes => Regions.Sum(r => r.Partition?.Volume?.UsedBytes ?? 0);
    public long FreeInsideVolumesBytes => Regions.Sum(r => r.Partition?.Volume?.FreeBytes ?? 0);
}

public static class DiskLayoutBuilder
{
    /// <summary>Regions smaller than this are treated as alignment slack, not usable space.</summary>
    public const long MinimumUsableUnallocated = ByteSize.MiB;

    /// <summary>Start of the disk reserved for partition-table structures.</summary>
    public const long LeadingReserved = ByteSize.MiB;

    public static DiskLayout Build(PhysicalDisk disk)
    {
        var problems = new List<string>();
        var regions = new List<DiskRegion>();

        if (disk.SizeBytes <= 0)
        {
            problems.Add("Windows reported a size of zero for this disk.");
            return new DiskLayout(disk, regions, problems);
        }

        // Extended containers hold logical partitions; lay out what is inside them, not the container.
        var topLevel = disk.Partitions
            .Where(p => p.Kind != PartitionKind.Extended)
            .OrderBy(p => p.OffsetBytes)
            .ToList();

        // The first MiB holds the partition table (and the MBR/GPT header). Windows aligns the first
        // partition after it, so space there is never offered as usable unallocated space.
        long cursor = Math.Min(LeadingReserved, Math.Min(disk.SizeBytes, topLevel.Count > 0 ? topLevel[0].OffsetBytes : LeadingReserved));
        if (cursor > 0) regions.Add(new DiskRegion(RegionKind.Gap, 0, cursor, null));

        foreach (var p in topLevel)
        {
            if (p.SizeBytes <= 0)
            {
                problems.Add($"Partition {p.PartitionNumber} has a reported size of zero.");
                continue;
            }
            if (p.OffsetBytes < cursor)
            {
                problems.Add($"Partition {p.PartitionNumber} overlaps the partition before it. The layout cannot be trusted.");
                continue;
            }
            if (p.EndBytes > disk.SizeBytes)
            {
                problems.Add($"Partition {p.PartitionNumber} extends past the end of the disk.");
                continue;
            }
            AddFree(regions, cursor, p.OffsetBytes - cursor);
            regions.Add(new DiskRegion(RegionKind.Partition, p.OffsetBytes, p.SizeBytes, p));
            cursor = p.EndBytes;
        }
        AddFree(regions, cursor, disk.SizeBytes - cursor);

        foreach (var p in disk.Partitions.Where(p => p.Volume is { } v && v.SizeBytes > p.SizeBytes))
            problems.Add($"Volume on partition {p.PartitionNumber} reports a size larger than its partition.");

        return new DiskLayout(disk, regions, problems);
    }

    private static void AddFree(List<DiskRegion> regions, long offset, long size)
    {
        if (size <= 0) return;
        var kind = size >= MinimumUsableUnallocated ? RegionKind.Unallocated : RegionKind.Gap;
        regions.Add(new DiskRegion(kind, offset, size, null));
    }
}
