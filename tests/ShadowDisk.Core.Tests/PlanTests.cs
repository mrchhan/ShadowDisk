using ShadowDisk.Core;
using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Planning;
using ShadowDisk.Core.Reporting;

namespace ShadowDisk.Core.Tests;

public class PlanTests
{
    private const long MiB = ByteSize.MiB;
    private const long GiB = ByteSize.GiB;

    private static DiskLayout Load(string fixture) => DiskLayoutBuilder.Build(FixtureInventory.LoadBuiltIn(fixture)[0]);

    private static DiskRegion RegionOf(DiskLayout layout, string letter) =>
        layout.Regions.Single(r => r.Partition?.Volume?.DriveLetter == letter);

    private static PlanResult Plan(DiskLayout layout, PlanKind kind, DiskRegion region, long target)
    {
        var limits = kind == PlanKind.CreateVolume
            ? PlanLimits.ForNewVolume(layout, region).Limits
            : PlanLimits.Estimate(layout, region.Partition!, kind);
        return PlanValidator.Validate(layout, new PlanRequest(kind, region, target), limits);
    }

    [Fact]
    public void Shrink_within_limits_is_valid_and_frees_unallocated_space_after_the_volume()
    {
        var layout = Load("desktop-hdd-mbr");
        var d = RegionOf(layout, "D");                       // 400 GiB, ~280 GiB used
        var result = Plan(layout, PlanKind.ShrinkVolume, d, 350 * GiB);

        Assert.True(result.IsValid);
        var proposed = result.ProposedLayout!;
        Assert.Equal(350 * GiB, result.ProposedRegion!.SizeBytes);
        Assert.Equal(proposed.Disk.SizeBytes, proposed.Regions.Sum(r => r.SizeBytes));
        // The 50 GiB freed sits directly after D:
        var after = proposed.Regions[proposed.Regions.ToList().IndexOf(result.ProposedRegion) + 1];
        Assert.Equal(RegionKind.Unallocated, after.Kind);
        Assert.Equal(50 * GiB, after.SizeBytes);
        // Used data is unchanged; free space inside D: shrinks.
        Assert.Equal(d.Partition!.Volume!.UsedBytes, result.ProposedRegion.Partition!.Volume!.UsedBytes);
        Assert.Contains("Shrink D:", result.Summary);
    }

    [Fact]
    public void Shrink_below_the_data_is_blocked_and_has_no_preview()
    {
        var layout = Load("desktop-hdd-mbr");
        var result = Plan(layout, PlanKind.ShrinkVolume, RegionOf(layout, "D"), 100 * GiB);
        Assert.False(result.IsValid);
        Assert.Null(result.ProposedLayout);
        Assert.Contains(result.Checks, c => c.Outcome == CheckOutcome.Failed && c.Text.Contains("below the minimum"));
        Assert.Contains(result.Checks, c => c.Outcome == CheckOutcome.Failed && c.Text.Contains("would not fit"));
    }

    [Fact]
    public void Unaligned_size_is_blocked()
    {
        var layout = Load("desktop-hdd-mbr");
        var result = Plan(layout, PlanKind.ShrinkVolume, RegionOf(layout, "D"), 350 * GiB + 123);
        Assert.Contains(result.Checks, c => c.Outcome == CheckOutcome.Failed && c.Text.Contains("whole number of MiB"));
    }

    [Fact]
    public void Estimated_limits_are_flagged_as_a_warning_not_hidden()
    {
        var layout = Load("desktop-hdd-mbr");
        var result = Plan(layout, PlanKind.ShrinkVolume, RegionOf(layout, "D"), 350 * GiB);
        Assert.Contains(result.Checks, c => c.Outcome == CheckOutcome.Warning && c.Text.Contains("Estimated"));
    }

    [Fact]
    public void Extend_into_free_space_after_a_volume()
    {
        // Shrink D: first (in a simulated layout), then extend it back into the space.
        var layout = Load("desktop-hdd-mbr");
        var shrunk = Plan(layout, PlanKind.ShrinkVolume, RegionOf(layout, "D"), 350 * GiB).ProposedLayout!;
        var d = RegionOf(shrunk, "D");

        var limits = PlanLimits.Estimate(shrunk, d.Partition!, PlanKind.ExtendVolume);
        Assert.Equal(400 * GiB, limits.MaxBytes);
        var result = PlanValidator.Validate(shrunk, new PlanRequest(PlanKind.ExtendVolume, d, 380 * GiB), limits);
        Assert.True(result.IsValid);
        Assert.Equal(380 * GiB, result.ProposedRegion!.SizeBytes);
    }

    [Fact]
    public void Extend_without_free_space_after_is_blocked()
    {
        var layout = Load("desktop-hdd-mbr");
        var d = RegionOf(layout, "D");   // E: follows D: directly
        var result = Plan(layout, PlanKind.ExtendVolume, d, 401 * GiB);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Exfat_volume_cannot_be_planned_even_with_free_space_after_it()
    {
        var layout = Load("desktop-hdd-mbr");
        var result = Plan(layout, PlanKind.ExtendVolume, RegionOf(layout, "E"), 250 * GiB);
        Assert.False(result.IsValid);
        Assert.Contains(result.Checks, c => c.Text.Contains("NTFS and ReFS"));
    }

    [Fact]
    public void Create_volume_in_unallocated_space_is_aligned_and_numbered()
    {
        var layout = Load("desktop-hdd-mbr");
        var free = layout.Regions.Single(r => r.Kind == RegionKind.Unallocated);
        var (offset, limits) = PlanLimits.ForNewVolume(layout, free);
        Assert.Equal(0, offset % MiB);
        Assert.True(limits.MaxBytes <= free.SizeBytes);

        var result = Plan(layout, PlanKind.CreateVolume, free, 100 * GiB);
        Assert.True(result.IsValid);
        var created = result.ProposedRegion!.Partition!;
        Assert.Equal(3, created.PartitionNumber);
        Assert.Equal("NTFS", created.Volume!.FileSystem);
        Assert.Equal(offset, created.OffsetBytes);
        Assert.True(result.ProposedLayout!.IsConsistent);
    }

    [Fact]
    public void Create_volume_larger_than_the_free_space_is_blocked()
    {
        var layout = Load("desktop-hdd-mbr");
        var free = layout.Regions.Single(r => r.Kind == RegionKind.Unallocated);
        Assert.False(Plan(layout, PlanKind.CreateVolume, free, free.SizeBytes + GiB).IsValid);
    }

    [Fact]
    public void Gpt_disk_keeps_room_for_the_backup_partition_table()
    {
        var disk = LayoutTests.Disk(10 * GiB, PartitionStyle.Gpt, LayoutTests.Part(1, MiB, 2 * GiB, v: LayoutTests.Ntfs(2 * GiB, GiB)));
        var layout = DiskLayoutBuilder.Build(disk);
        var free = layout.Regions.Single(r => r.Kind == RegionKind.Unallocated);
        var (offset, limits) = PlanLimits.ForNewVolume(layout, free);
        Assert.True(offset + limits.MaxBytes <= disk.SizeBytes - PlanLimits.GptTailReserve);
    }

    [Fact]
    public void Protected_and_unsupported_partitions_cannot_be_planned()
    {
        var laptop = Load("laptop-nvme-gpt");
        var efi = laptop.Regions.Single(r => r.Partition?.Kind == PartitionKind.EfiSystem);
        Assert.False(Plan(laptop, PlanKind.ShrinkVolume, efi, 50 * MiB).IsValid);

        var dynamicDisk = Load("dynamic-disk");
        var data = dynamicDisk.Regions.Single(r => r.Partition?.Kind == PartitionKind.LdmData);
        Assert.False(PlanValidator.Validate(dynamicDisk,
            new PlanRequest(PlanKind.ShrinkVolume, data, GiB), new SizeLimits(MiB, 2 * GiB, LimitsSource.Estimated)).IsValid);
    }

    [Fact]
    public void Windows_volume_and_bitlocker_produce_warnings_and_extra_risk()
    {
        var laptop = Load("laptop-nvme-gpt");
        var c = RegionOf(laptop, "C");
        var result = Plan(laptop, PlanKind.ShrinkVolume, c, 400 * GiB);
        Assert.True(result.IsValid);
        Assert.Contains(result.Checks, x => x.Outcome == CheckOutcome.Warning && x.Text.Contains("BitLocker"));
        Assert.Contains(result.Checks, x => x.Outcome == CheckOutcome.Warning && x.Text.Contains("Windows runs from this volume"));
        Assert.Contains(result.Risks, r => r.Contains("stop the PC from starting"));
        Assert.Contains(result.Risks, r => r.Contains("No disk tool can guarantee"));
    }

    [Fact]
    public void Windows_limits_are_respected_and_capped_by_the_layout()
    {
        var layout = Load("desktop-hdd-mbr");
        var d = RegionOf(layout, "D").Partition!;
        // Windows says D: can't shrink below 300 GiB (unmovable files), more than our estimate.
        var shrink = PlanLimits.FromWindows(layout, d, PlanKind.ShrinkVolume, 300 * GiB, 400 * GiB);
        Assert.Equal(300 * GiB, shrink.MinBytes);
        Assert.Equal(LimitsSource.Windows, shrink.Source);
        // Windows' max can never let a plan reach past the free space after the volume (none here).
        var extend = PlanLimits.FromWindows(layout, d, PlanKind.ExtendVolume, 400 * GiB, 900 * GiB);
        Assert.False(extend.IsUsable);
    }

    [Fact]
    public void Estimate_never_goes_below_the_data()
    {
        var layout = Load("failing-hdd");
        var g = RegionOf(layout, "G").Partition!;
        var limits = PlanLimits.Estimate(layout, g, PlanKind.ShrinkVolume);
        Assert.True(limits.MinBytes >= PlanLimits.DataFootprint(g));
        Assert.Equal(0, limits.MinBytes % MiB);
    }

    [Fact]
    public void Volume_with_too_little_room_cannot_shrink()
    {
        var disk = LayoutTests.Disk(10 * GiB, PartitionStyle.Gpt,
            LayoutTests.Part(1, MiB, 2 * GiB, v: LayoutTests.Ntfs(2 * GiB, 100 * MiB)));
        var layout = DiskLayoutBuilder.Build(disk);
        var limits = PlanLimits.Estimate(layout, disk.Partitions[0], PlanKind.ShrinkVolume);
        Assert.False(limits.IsUsable);
    }

    [Fact]
    public async Task Estimated_provider_honours_cancellation()
    {
        var layout = Load("desktop-hdd-mbr");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new EstimatedLimitsProvider().GetLimitsAsync(layout, layout.Disk.Partitions[0], PlanKind.ShrinkVolume, cts.Token));
    }

    [Fact]
    public async Task Report_marks_plan_as_preview_only()
    {
        var layout = Load("desktop-hdd-mbr");
        var plan = Plan(layout, PlanKind.ShrinkVolume, RegionOf(layout, "D"), 350 * GiB);
        var snap = await FixtureInventory.DemoAll().GetSnapshotAsync(CancellationToken.None);
        var text = ReportWriter.Write(snap, "0.2.0", plan);
        Assert.Contains("PREVIEW ONLY. NOTHING HAS BEEN APPLIED", text);
        Assert.Contains("Passes all checks", text);
    }

    [Fact]
    public void Tiny_free_space_is_not_offered_for_extend_or_new_volume()
    {
        // Like the real test PC: 1.86 MiB unallocated after the last volume.
        var disk = LayoutTests.Disk(10 * GiB, PartitionStyle.Mbr,
            LayoutTests.Part(1, MiB, 10 * GiB - MiB - 1950000, v: LayoutTests.Ntfs(10 * GiB - MiB - 1950000, 5 * GiB, "E")));
        var layout = DiskLayoutBuilder.Build(disk);
        var tail = layout.Regions.Single(r => r.Kind == RegionKind.Unallocated);
        Assert.True(tail.SizeBytes < Safety.SafetyAnalyzer.MinimumUsefulSpace);

        var extend = Safety.SafetyAnalyzer.AssessPartition(layout, disk.Partitions[0]).Single(a => a.Action == Safety.PlannedAction.ExtendVolume);
        Assert.False(extend.PassesBasicChecks);
        Assert.Contains(extend.Reasons, r => r.Contains("too little"));
        Assert.False(Safety.SafetyAnalyzer.AssessUnallocated(layout, tail).PassesBasicChecks);
    }

    [Fact]
    public void Planning_never_modifies_the_current_layout()
    {
        var layout = Load("desktop-hdd-mbr");
        var before = layout.Disk.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes)).ToList();
        Plan(layout, PlanKind.ShrinkVolume, RegionOf(layout, "D"), 350 * GiB);
        Plan(layout, PlanKind.CreateVolume, layout.Regions.Single(r => r.Kind == RegionKind.Unallocated), 10 * GiB);
        Assert.Equal(before, layout.Disk.Partitions.Select(p => (p.OffsetBytes, p.SizeBytes)).ToList());
    }
}
