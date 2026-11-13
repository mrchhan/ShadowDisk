using System.Globalization;
using System.Text;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Safety;

namespace ShadowDisk.Core.Reporting;

/// <summary>Plain-text inventory report. Serial numbers are masked; file names and folder contents are never read.</summary>
public static class ReportWriter
{
    public static string Write(InventorySnapshot snapshot, string appVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SHADOWDISK — DISK INVENTORY REPORT");
        sb.AppendLine($"App version : {appVersion}");
        sb.AppendLine($"Created     : {snapshot.TakenAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Source      : {snapshot.SourceDescription}{(snapshot.IsSimulated ? "  *** SIMULATED DATA — NOT YOUR REAL DISKS ***" : "")}");
        sb.AppendLine($"Mode        : {SafetyAnalyzer.ReadOnlyStatement}");
        sb.AppendLine("Sizes       : GiB = 1,073,741,824 bytes. Exact byte counts are shown in brackets.");
        foreach (var w in snapshot.Warnings) sb.AppendLine($"Warning     : {w}");
        sb.AppendLine();

        foreach (var disk in snapshot.Disks)
        {
            var layout = DiskLayoutBuilder.Build(disk);
            sb.AppendLine(new string('=', 72));
            sb.AppendLine($"Disk {disk.Number}: {disk.FriendlyName}");
            sb.AppendLine($"  Serial          : {StorageMapper.MaskSerial(disk.SerialNumber) ?? "not reported"}");
            sb.AppendLine($"  Size            : {ByteSize.FormatWithExact(disk.SizeBytes)}");
            sb.AppendLine($"  Partition style : {VisualSegmentBuilder.StyleText(disk.Style)}");
            sb.AppendLine($"  Connection      : {VisualSegmentBuilder.BusText(disk.Bus)}");
            sb.AppendLine($"  Sectors         : {disk.LogicalSectorSize} bytes logical / {disk.PhysicalSectorSize} bytes physical");
            sb.AppendLine($"  Windows health  : {disk.Health} (reported by Windows; this is not a surface scan)");
            sb.AppendLine($"  Allocated       : {ByteSize.FormatWithExact(layout.AllocatedBytes)}");
            sb.AppendLine($"  Unallocated     : {ByteSize.FormatWithExact(layout.UnallocatedBytes)}");
            sb.AppendLine();
            sb.AppendLine("  Layout (in disk order):");
            foreach (var r in layout.Regions.Where(r => r.Kind != RegionKind.Gap))
            {
                if (r.Partition is { } p)
                {
                    sb.Append($"    [{p.PartitionNumber}] {p.DisplayName,-20} {p.Kind,-18} {ByteSize.Format(r.SizeBytes),12}");
                    if (p.Volume is { } v)
                        sb.Append($"  {v.FileSystem ?? "?",-6} used {ByteSize.Format(v.UsedBytes)}, free {ByteSize.Format(v.FreeBytes)}, encryption {v.Encryption}");
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine($"    [ ] {"Unallocated",-20} {"",-18} {ByteSize.Format(r.SizeBytes),12}");
                }
            }
            var notices = SafetyAnalyzer.AnalyzeDisk(layout);
            if (notices.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  Notices:");
                foreach (var n in notices)
                    sb.AppendLine($"    - [{n.Severity}] {(n.PartitionNumber is { } pn ? $"Partition {pn}: " : "")}{n.Title}. {n.Detail}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
