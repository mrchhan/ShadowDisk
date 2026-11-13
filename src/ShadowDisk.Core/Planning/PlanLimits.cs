using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Planning;

/// <summary>Size-limit arithmetic. All results are whole MiB (1 MiB alignment, as Windows uses).</summary>
public static class PlanLimits
{
    public const long Alignment = ByteSize.MiB;
    public const long MinimumNewVolume = 16 * ByteSize.MiB;

    /// <summary>GPT keeps a backup partition table in the last sectors of the disk.</summary>
    public const long GptTailReserve = ByteSize.MiB;

    public static long AlignDown(long bytes) => bytes <= 0 ? 0 : bytes / Alignment * Alignment;
    public static long AlignUp(long bytes) => bytes <= 0 ? 0 : (bytes + Alignment - 1) / Alignment * Alignment;

    /// <summary>Space the volume's file system doesn't report as its own (partition size minus volume size).</summary>
    public static long Overhead(Partition p) => p.Volume is { } v ? Math.Max(0, p.SizeBytes - v.SizeBytes) : 0;

    /// <summary>Bytes the partition needs to keep all its data: used data plus file-system overhead.</summary>
    public static long DataFootprint(Partition p) => (p.Volume?.UsedBytes ?? p.SizeBytes) + Overhead(p);

    /// <summary>Free space directly after the partition, up to the next partition (unallocated plus small gaps).</summary>
    public static long FreeSpaceAfter(DiskLayout layout, Partition p)
    {
        var regions = layout.Regions;
        var index = regions.ToList().FindIndex(r => ReferenceEquals(r.Partition, p));
        if (index < 0) return 0;
        long total = 0;
        var sawUnallocated = false;
        for (var i = index + 1; i < regions.Count && regions[i].Partition is null; i++)
        {
            total += regions[i].SizeBytes;
            sawUnallocated |= regions[i].Kind == RegionKind.Unallocated;
        }
        if (!sawUnallocated) return 0;
        if (layout.Disk.Style == PartitionStyle.Gpt && p.EndBytes + total >= layout.Disk.SizeBytes)
            total = Math.Max(0, total - GptTailReserve);
        return total;
    }

    /// <summary>
    /// ShadowDisk's own estimate when Windows can't be asked. Deliberately cautious: data plus
    /// 10% (at least 1 GiB) headroom. Windows' real minimum may still be higher because some
    /// files (page file, shadow copies, file-system metadata) can't be moved.
    /// </summary>
    public static SizeLimits Estimate(DiskLayout layout, Partition p, PlanKind kind, string? note = null)
    {
        switch (kind)
        {
            case PlanKind.ShrinkVolume:
            {
                var used = p.Volume?.UsedBytes ?? p.SizeBytes;
                var min = AlignUp(DataFootprint(p) + Math.Max(ByteSize.GiB, used / 10));
                var max = AlignDown(p.SizeBytes - Alignment);
                return new SizeLimits(Math.Min(min, p.SizeBytes), max, LimitsSource.Estimated,
                    note ?? "Estimated by ShadowDisk. Windows' real minimum may be higher because some files can't be moved.");
            }
            case PlanKind.ExtendVolume:
            {
                var free = AlignDown(FreeSpaceAfter(layout, p));
                return new SizeLimits(p.SizeBytes + Alignment, p.SizeBytes + free, LimitsSource.Estimated,
                    note ?? "Calculated from the unallocated space directly after this volume.");
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), "Use ForNewVolume for new volumes.");
        }
    }

    /// <summary>
    /// Combines the sizes Windows reports (GetSupportedSize) with the layout. Windows decides the
    /// shrink minimum; the layout caps the extend maximum so a plan never reaches past free space.
    /// </summary>
    public static SizeLimits FromWindows(DiskLayout layout, Partition p, PlanKind kind, long windowsMin, long windowsMax)
    {
        switch (kind)
        {
            case PlanKind.ShrinkVolume:
            {
                var min = Math.Max(AlignUp(windowsMin), AlignUp(DataFootprint(p)));
                return new SizeLimits(Math.Min(min, p.SizeBytes), AlignDown(p.SizeBytes - Alignment), LimitsSource.Windows,
                    "Minimum reported by Windows, which accounts for files that can't be moved.");
            }
            case PlanKind.ExtendVolume:
            {
                var layoutMax = p.SizeBytes + AlignDown(FreeSpaceAfter(layout, p));
                var max = Math.Min(layoutMax, Math.Max(p.SizeBytes, AlignDown(windowsMax)));
                return new SizeLimits(p.SizeBytes + Alignment, max, LimitsSource.Windows,
                    "Maximum reported by Windows (limited to the free space directly after this volume).");
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>Where a new volume would start, and the most it could hold, inside an unallocated region.</summary>
    public static (long Offset, SizeLimits Limits) ForNewVolume(DiskLayout layout, DiskRegion region)
    {
        var start = AlignUp(region.OffsetBytes);
        var end = region.EndBytes;
        if (layout.Disk.Style == PartitionStyle.Gpt && end >= layout.Disk.SizeBytes) end -= GptTailReserve;
        var max = AlignDown(end - start);
        return (start, new SizeLimits(MinimumNewVolume, Math.Max(0, max), LimitsSource.FreeSpace,
            "Calculated from the unallocated space, aligned to 1 MiB."));
    }
}

/// <summary>Limits from ShadowDisk's own arithmetic. Used for demo disks and when Windows can't be asked.</summary>
public sealed class EstimatedLimitsProvider : IPartitionLimitsProvider
{
    public Task<SizeLimits> GetLimitsAsync(DiskLayout layout, Partition partition, PlanKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PlanLimits.Estimate(layout, partition, kind));
    }
}
