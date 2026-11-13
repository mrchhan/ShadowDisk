using ShadowDisk.Core;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Safety;

namespace ShadowDisk.App.ViewModels;

public sealed class DiskItemViewModel
{
    public DiskItemViewModel(PhysicalDisk disk, bool isPractice = false)
    {
        Disk = disk;
        IsPractice = isPractice;
        Layout = DiskLayoutBuilder.Build(disk);
        var notices = SafetyAnalyzer.AnalyzeDisk(Layout).Select(n => new NoticeViewModel(n)).ToList();
        if (isPractice)
            notices.Insert(0, new NoticeViewModel(new Notice(NoticeSeverity.Info, "ShadowDisk practice disk",
                "A virtual disk file created by ShadowDisk for safe testing. Real changes are allowed here. Remove it with “Remove practice disks”.")));
        Notices = notices;
        Rows = Layout.Regions
            .Where(r => r.Kind != RegionKind.Gap)
            .Select(r => new RegionRowViewModel(Layout, r))
            .ToList();
    }

    public PhysicalDisk Disk { get; }
    public bool IsPractice { get; }
    public DiskLayout Layout { get; }
    public IReadOnlyList<NoticeViewModel> Notices { get; }
    public IReadOnlyList<RegionRowViewModel> Rows { get; }

    public string Title => $"Disk {Disk.Number} — {Disk.FriendlyName}";
    public string Subtitle => $"{ByteSize.Format(Disk.SizeBytes)} · {VisualSegmentBuilder.StyleText(Disk.Style)} · {BusText(Disk.Bus)}";
    public string Badges => string.Join("  ", new[]
    {
        IsPractice ? "Practice disk" : null,
        Disk.IsSystem || Disk.IsBoot ? "Windows" : null,
        Disk.IsRemovableBus ? "Removable" : null,
        Disk.IsDynamic ? "Dynamic (unsupported)" : null,
        Disk.Health is ReportedHealth.Warning or ReportedHealth.Unhealthy ? $"Health: {Disk.Health}" : null,
        Disk.IsOffline ? "Offline" : null,
    }.Where(s => s is not null));

    public string SizeText => ByteSize.FormatWithExact(Disk.SizeBytes);
    public string StyleText => VisualSegmentBuilder.StyleText(Disk.Style);
    public string BusDisplay => BusText(Disk.Bus);
    public string SectorText => $"{Disk.LogicalSectorSize} bytes logical / {Disk.PhysicalSectorSize} bytes physical";
    public string SerialText => StorageMapper.MaskSerial(Disk.SerialNumber) ?? "not reported";
    public string HealthText => Disk.Health switch
    {
        ReportedHealth.Healthy => "Healthy",
        ReportedHealth.Warning => "Warning — back up this disk",
        ReportedHealth.Unhealthy => "Unhealthy — copy your data off this disk",
        _ => "Not reported",
    };

    public string UsedText => ByteSize.Format(Layout.UsedDataBytes);
    public string FreeText => ByteSize.Format(Layout.FreeInsideVolumesBytes);
    public string UnallocatedText => ByteSize.Format(Layout.UnallocatedBytes);
    public string AllocatedText => ByteSize.Format(Layout.AllocatedBytes);
    public string AccessibleDescription => VisualSegmentBuilder.Describe(Layout);

    public static string BusText(BusType bus) => VisualSegmentBuilder.BusText(bus);
}

public sealed class RegionRowViewModel
{
    public RegionRowViewModel(DiskLayout layout, DiskRegion region)
    {
        Region = region;
        var p = region.Partition;
        var v = p?.Volume;

        Number = p?.PartitionNumber.ToString() ?? "—";
        Name = p is null ? "Unallocated"
            : p.DisplayName + (v?.DriveLetter is not null && v.Label is { } label ? $" ({label})" : "");
        Type = p is null ? "Not part of any partition"
            : p.IsSystem && p.Kind is PartitionKind.Basic or PartitionKind.OemOrHidden or PartitionKind.Unknown ? "System (protected)"
            : KindText(p.Kind);
        FileSystem = v?.FileSystem ?? (p is null ? "" : "—");
        Start = ByteSize.Format(region.OffsetBytes);
        Size = ByteSize.Format(region.SizeBytes);
        SizeExact = ByteSize.FormatWithExact(region.SizeBytes);
        Used = v is null ? "—" : ByteSize.Format(v.UsedBytes);
        Free = v is null ? (p is null ? Size : "—") : ByteSize.Format(v.FreeBytes);
        Encryption = v?.Encryption switch
        {
            EncryptionState.Encrypted => "BitLocker on",
            EncryptionState.Locked => "Locked",
            EncryptionState.NotEncrypted => "Off",
            EncryptionState.Unknown => "Unknown",
            _ => "",
        };

        Notices = p is null ? [] : SafetyAnalyzer.AnalyzePartition(p).Select(n => new NoticeViewModel(n)).ToList();
        IReadOnlyList<ActionAssessment> assessments = p is null
            ? [SafetyAnalyzer.AssessUnallocated(layout, region)]
            : SafetyAnalyzer.AssessPartition(layout, p);
        Assessments = assessments.Select(a => new AssessmentViewModel(a)).ToList();
    }

    public DiskRegion Region { get; }
    public string Number { get; }
    public string Name { get; }
    public string Type { get; }
    public string FileSystem { get; }
    public string Start { get; }
    public string Size { get; }
    public string SizeExact { get; }
    public string Used { get; }
    public string Free { get; }
    public string Encryption { get; }
    public IReadOnlyList<NoticeViewModel> Notices { get; }
    public IReadOnlyList<AssessmentViewModel> Assessments { get; }

    public static string KindText(PartitionKind k) => k switch
    {
        PartitionKind.Basic => "Basic data",
        PartitionKind.Logical => "Logical (inside extended)",
        PartitionKind.EfiSystem => "EFI System (protected)",
        PartitionKind.MicrosoftReserved => "Microsoft Reserved (protected)",
        PartitionKind.Recovery => "Recovery",
        PartitionKind.LdmMetadata => "Dynamic disk metadata",
        PartitionKind.LdmData => "Dynamic disk data",
        PartitionKind.DynamicMbr => "Dynamic disk",
        PartitionKind.Extended => "Extended container",
        PartitionKind.OemOrHidden => "Other / unrecognised",
        _ => "Unknown",
    };
}

public sealed class NoticeViewModel(Notice notice)
{
    public string Title => notice.Title;
    public string Detail => notice.Detail;
    public string Severity => notice.Severity switch
    {
        NoticeSeverity.Blocking => "Protected",
        NoticeSeverity.Caution => "Caution",
        _ => "Info",
    };
    public string Scope => notice.PartitionNumber is { } n ? $"Partition {n}" : "Disk";
    public string Color => notice.Severity switch
    {
        NoticeSeverity.Blocking => "#B91C1C",
        NoticeSeverity.Caution => "#B45309",
        _ => "#1D4ED8",
    };
}

public sealed class AssessmentViewModel(ActionAssessment a)
{
    public string Action => a.Action switch
    {
        PlannedAction.ShrinkVolume => "Shrink volume",
        PlannedAction.ExtendVolume => "Extend volume",
        PlannedAction.CreateVolume => "Create a new volume here",
        _ => a.Action.ToString(),
    };

    public string Verdict => a.PassesBasicChecks
        ? "Passes basic checks. Use “Plan a change” above to preview it."
        : "Would not be offered:";

    public IReadOnlyList<string> Reasons => a.Reasons;
}
