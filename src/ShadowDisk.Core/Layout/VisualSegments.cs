using System.Text;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Layout;

public enum SegmentKind
{
    UsedData,          // Files stored inside a volume
    FreeInVolume,      // Free space inside a volume (already allocated to that partition)
    SystemOrReserved,  // EFI, MSR, recovery, partitions without a readable volume
    Unallocated,       // Disk space not belonging to any partition
}

public sealed record VisualSegment(SegmentKind Kind, string Label, long Bytes, Partition? Partition, DiskRegion Region);

/// <summary>One block of the bar: a partition or an unallocated region, with its used/free split.</summary>
public sealed record BarBlock(DiskRegion Region, string Label, long UsedBytes, long FreeBytes)
{
    public double UsedFraction => Region.SizeBytes == 0 ? 0 : (double)UsedBytes / Region.SizeBytes;
}

public static class VisualSegmentBuilder
{
    public static IReadOnlyList<BarBlock> BuildBar(DiskLayout layout) =>
        layout.Regions
            .Where(r => r.Kind != RegionKind.Gap)
            .Select(r => r.Partition is { Volume: { } v } p
                ? new BarBlock(r, p.DisplayName, Math.Min(v.UsedBytes, r.SizeBytes), Math.Max(0, r.SizeBytes - v.UsedBytes))
                : new BarBlock(r, r.Partition?.DisplayName ?? "Unallocated", 0, 0))
            .ToList();

    /// <summary>Slices for a pie/donut of the whole disk. Each volume contributes a used and a free slice.</summary>
    public static IReadOnlyList<VisualSegment> BuildPie(DiskLayout layout)
    {
        var list = new List<VisualSegment>();
        foreach (var r in layout.Regions.Where(r => r.Kind != RegionKind.Gap))
        {
            if (r.Partition is { Volume: { } v } p)
            {
                var used = Math.Min(v.UsedBytes, r.SizeBytes);
                list.Add(new VisualSegment(SegmentKind.UsedData, $"{p.DisplayName} used", used, p, r));
                list.Add(new VisualSegment(SegmentKind.FreeInVolume, $"{p.DisplayName} free", r.SizeBytes - used, p, r));
            }
            else if (r.Partition is { } sp)
            {
                list.Add(new VisualSegment(SegmentKind.SystemOrReserved, sp.DisplayName, r.SizeBytes, sp, r));
            }
            else
            {
                list.Add(new VisualSegment(SegmentKind.Unallocated, "Unallocated", r.SizeBytes, null, r));
            }
        }
        return list.Where(s => s.Bytes > 0).ToList();
    }

    /// <summary>
    /// Converts sizes into display lengths (pixels, degrees…) so tiny partitions such as a 16 MiB MSR on a
    /// 2 TiB disk stay visible. Display only — the textual table always shows the exact sizes.
    /// </summary>
    public static double[] ScaleWithMinimum(IReadOnlyList<long> sizes, double total, double minimum)
    {
        var n = sizes.Count;
        var result = new double[n];
        if (n == 0 || total <= 0) return result;
        if (minimum * n >= total)
        {
            Array.Fill(result, total / n);
            return result;
        }

        var pinned = new bool[n];
        while (true)
        {
            var freeTotal = total - pinned.Count(p => p) * minimum;
            double freeBytes = 0;
            for (var i = 0; i < n; i++) if (!pinned[i]) freeBytes += Math.Max(0, sizes[i]);

            var changed = false;
            for (var i = 0; i < n; i++)
            {
                if (pinned[i]) { result[i] = minimum; continue; }
                result[i] = freeBytes == 0 ? freeTotal / pinned.Count(p => !p) : freeTotal * Math.Max(0, sizes[i]) / freeBytes;
                if (result[i] < minimum) { pinned[i] = true; changed = true; }
            }
            if (!changed) return result;
        }
    }

    /// <summary>Plain-text description of the layout for screen readers and the report.</summary>
    public static string Describe(DiskLayout layout)
    {
        var d = layout.Disk;
        var sb = new StringBuilder();
        sb.Append($"Disk {d.Number}, {d.FriendlyName}, {ByteSize.Format(d.SizeBytes)}, {StyleText(d.Style)}. ");
        var parts = layout.Regions.Count(r => r.Kind == RegionKind.Partition);
        sb.Append($"{parts} partition{(parts == 1 ? "" : "s")}. ");
        foreach (var r in layout.Regions.Where(r => r.Kind != RegionKind.Gap))
        {
            if (r.Partition is { } p)
            {
                sb.Append($"{p.DisplayName}: {ByteSize.Format(r.SizeBytes)}");
                if (p.Volume is { } v)
                    sb.Append($", {v.FileSystem ?? "unknown file system"}, {ByteSize.Format(v.UsedBytes)} used, {ByteSize.Format(v.FreeBytes)} free");
                sb.Append(". ");
            }
            else
            {
                sb.Append($"Unallocated: {ByteSize.Format(r.SizeBytes)}. ");
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static string BusText(BusType bus) => bus switch
    {
        BusType.Nvme => "NVMe",
        BusType.Sata => "SATA",
        BusType.Ata => "ATA",
        BusType.Usb => "USB",
        BusType.SdCard => "SD card",
        BusType.Mmc => "MMC",
        BusType.Sas => "SAS",
        BusType.Scsi => "SCSI",
        BusType.Raid => "RAID",
        BusType.Virtual or BusType.FileBackedVirtual => "Virtual disk",
        _ => "Unknown connection",
    };

    public static string StyleText(PartitionStyle s) => s switch
    {
        PartitionStyle.Gpt => "GPT",
        PartitionStyle.Mbr => "MBR",
        PartitionStyle.Raw => "not initialized",
        _ => "unknown partition style",
    };
}
