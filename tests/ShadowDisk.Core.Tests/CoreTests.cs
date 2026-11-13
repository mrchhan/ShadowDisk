using ShadowDisk.Core;
using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Reporting;
using ShadowDisk.Core.Safety;

namespace ShadowDisk.Core.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1.00 KiB")]
    [InlineData(1073741824, "1.00 GiB")]
    [InlineData(512110190592, "476.93 GiB")]
    public void Formats_binary_units_explicitly(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Never_rounds_up() =>
        // 1.999... GiB must not be shown as 2.00 GiB.
        Assert.Equal("1.99 GiB", ByteSize.Format(2 * ByteSize.GiB - 1));

    [Fact]
    public void Decimal_units_match_drive_box_labels() =>
        Assert.Equal("1.00 TB", ByteSize.Format(1000204886016, binary: false));

    [Fact]
    public void Negative_sizes_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteSize.Format(-1));

    [Fact]
    public void Alignment_check()
    {
        Assert.True(ByteSize.IsAligned(1048576, ByteSize.MiB));
        Assert.False(ByteSize.IsAligned(17408, ByteSize.MiB));
    }
}

public class FixtureTests
{
    [Fact]
    public void All_built_in_fixtures_load_and_have_consistent_layouts()
    {
        var names = FixtureInventory.BuiltInFixtureNames();
        Assert.Contains("laptop-nvme-gpt", names);
        Assert.True(names.Count >= 6);
        foreach (var name in names)
            foreach (var disk in FixtureInventory.LoadBuiltIn(name))
                Assert.True(DiskLayoutBuilder.Build(disk).IsConsistent, name);
    }

    [Fact]
    public async Task Demo_inventory_is_marked_simulated_with_unique_disk_numbers()
    {
        var snap = await FixtureInventory.DemoAll().GetSnapshotAsync(CancellationToken.None);
        Assert.True(snap.IsSimulated);
        Assert.Equal(snap.Disks.Count, snap.Disks.Select(d => d.Number).Distinct().Count());
        Assert.All(snap.Disks, d => Assert.All(d.Partitions, p => Assert.Equal(d.Number, p.DiskNumber)));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => FixtureInventory.DemoAll().GetSnapshotAsync(cts.Token));
    }

    [Fact]
    public void Unknown_fixture_name_throws() =>
        Assert.Throws<ArgumentException>(() => FixtureInventory.LoadBuiltIn("does-not-exist"));
}

public class LayoutTests
{
    internal static PhysicalDisk Disk(long size, PartitionStyle style, params Partition[] parts) =>
        new(0, "Test", "SERIAL123456", size, 512, 4096, style, BusType.Sata, false, false, false, false, ReportedHealth.Healthy, parts);

    internal static Partition Part(int n, long offset, long size, PartitionKind kind = PartitionKind.Basic, Volume? v = null) =>
        new(0, n, offset, size, kind, null, null, false, false, false, false, v);

    internal static Volume Ntfs(long size, long free, string letter = "D") =>
        new(letter, null, "NTFS", size, free, ReportedHealth.Healthy, EncryptionState.NotEncrypted);

    [Fact]
    public void Finds_unallocated_space_between_and_after_partitions()
    {
        var disk = Disk(100 * ByteSize.GiB, PartitionStyle.Gpt,
            Part(1, ByteSize.MiB, 10 * ByteSize.GiB),
            Part(2, 30 * ByteSize.GiB, 10 * ByteSize.GiB));
        var layout = DiskLayoutBuilder.Build(disk);

        Assert.True(layout.IsConsistent);
        var kinds = layout.Regions.Select(r => r.Kind).ToArray();
        Assert.Equal([RegionKind.Gap, RegionKind.Partition, RegionKind.Unallocated, RegionKind.Partition, RegionKind.Unallocated], kinds);
        Assert.Equal(100 * ByteSize.GiB - ByteSize.MiB - 20 * ByteSize.GiB, layout.UnallocatedBytes);
        Assert.Equal(disk.SizeBytes, layout.Regions.Sum(r => r.SizeBytes));
    }

    [Fact]
    public void Treats_slack_under_one_MiB_as_a_gap_not_usable_space()
    {
        var disk = Disk(10 * ByteSize.GiB, PartitionStyle.Gpt, Part(1, ByteSize.MiB, 10 * ByteSize.GiB - ByteSize.MiB - 4096));
        var layout = DiskLayoutBuilder.Build(disk);
        Assert.Equal(0, layout.UnallocatedBytes);
        Assert.Equal(RegionKind.Gap, layout.Regions[^1].Kind);
    }

    [Fact]
    public void Overlapping_partitions_make_the_layout_untrusted()
    {
        var disk = Disk(10 * ByteSize.GiB, PartitionStyle.Gpt,
            Part(1, ByteSize.MiB, 5 * ByteSize.GiB),
            Part(2, 4 * ByteSize.GiB, 2 * ByteSize.GiB));
        var layout = DiskLayoutBuilder.Build(disk);
        Assert.False(layout.IsConsistent);
        Assert.Contains(SafetyAnalyzer.AnalyzeDisk(layout), n => n.Severity == NoticeSeverity.Blocking);
    }

    [Fact]
    public void Partition_past_end_of_disk_is_reported()
    {
        var disk = Disk(10 * ByteSize.GiB, PartitionStyle.Gpt, Part(1, ByteSize.MiB, 10 * ByteSize.GiB));
        Assert.False(DiskLayoutBuilder.Build(disk).IsConsistent);
    }

    [Fact]
    public void Extended_container_is_not_drawn_but_its_logical_partitions_are()
    {
        var disk = Disk(100 * ByteSize.GiB, PartitionStyle.Mbr,
            Part(1, ByteSize.MiB, 10 * ByteSize.GiB),
            Part(2, 20 * ByteSize.GiB, 50 * ByteSize.GiB, PartitionKind.Extended),
            Part(5, 20 * ByteSize.GiB + ByteSize.MiB, 20 * ByteSize.GiB, PartitionKind.Logical));
        var layout = DiskLayoutBuilder.Build(disk);
        Assert.True(layout.IsConsistent);
        Assert.DoesNotContain(layout.Regions, r => r.Partition?.Kind == PartitionKind.Extended);
        Assert.Contains(layout.Regions, r => r.Partition?.PartitionNumber == 5);
    }

    [Fact]
    public void Zero_size_disk_is_reported() =>
        Assert.False(DiskLayoutBuilder.Build(Disk(0, PartitionStyle.Gpt)).IsConsistent);

    [Fact]
    public void Separates_used_data_free_space_and_unallocated()
    {
        var disk = Disk(100 * ByteSize.GiB, PartitionStyle.Gpt,
            Part(1, ByteSize.MiB, 50 * ByteSize.GiB, v: Ntfs(50 * ByteSize.GiB, 20 * ByteSize.GiB)));
        var layout = DiskLayoutBuilder.Build(disk);
        Assert.Equal(30 * ByteSize.GiB, layout.UsedDataBytes);
        Assert.Equal(20 * ByteSize.GiB, layout.FreeInsideVolumesBytes);
        Assert.Equal(50 * ByteSize.GiB - ByteSize.MiB, layout.UnallocatedBytes);

        var pie = VisualSegmentBuilder.BuildPie(layout);
        Assert.Equal([SegmentKind.UsedData, SegmentKind.FreeInVolume, SegmentKind.Unallocated], pie.Select(s => s.Kind).ToArray());
        Assert.Equal(disk.SizeBytes - ByteSize.MiB, pie.Sum(s => s.Bytes)); // leading 1 MiB gap is not drawn

        var bar = VisualSegmentBuilder.BuildBar(layout);
        Assert.Equal(0.6, bar[0].UsedFraction, 3);
    }
}

public class ScalingTests
{
    [Fact]
    public void Keeps_tiny_partitions_visible_and_fills_the_total()
    {
        long[] sizes = [16 * ByteSize.MiB, 2 * ByteSize.TiB];
        var widths = VisualSegmentBuilder.ScaleWithMinimum(sizes, 800, 8);
        Assert.Equal(8, widths[0], 6);
        Assert.Equal(800, widths.Sum(), 6);
    }

    [Fact]
    public void Proportional_when_nothing_is_tiny()
    {
        var widths = VisualSegmentBuilder.ScaleWithMinimum([100, 300], 400, 8);
        Assert.Equal([100.0, 300.0], widths);
    }

    [Fact]
    public void Equal_split_when_minimums_cannot_fit()
    {
        var widths = VisualSegmentBuilder.ScaleWithMinimum([1, 1, 1000], 20, 10);
        Assert.All(widths, w => Assert.Equal(20.0 / 3, w, 6));
    }

    [Fact]
    public void Empty_input() => Assert.Empty(VisualSegmentBuilder.ScaleWithMinimum([], 100, 5));
}

public class MappingTests
{
    private static RawDisk RawDisk(ushort style = 2, ushort bus = 17) =>
        new(0, "Disk", "SN-ABCDEF", 100UL * 1024 * 1024 * 1024, 512, 4096, style, bus, true, true, false, false, 0);

    [Theory]
    [InlineData("{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}", PartitionKind.EfiSystem)]
    [InlineData("e3c9e316-0b5c-4db8-817d-f92df00215ae", PartitionKind.MicrosoftReserved)]
    [InlineData("{de94bba4-06d1-4d40-a16a-bfd50179d6ac}", PartitionKind.Recovery)]
    [InlineData("{5808c8aa-7e8f-42e0-85d2-e1e90434cfb3}", PartitionKind.LdmMetadata)]
    [InlineData("{0fc63daf-8483-4772-8e79-3d69d8477de4}", PartitionKind.OemOrHidden)]
    [InlineData(null, PartitionKind.Unknown)]
    public void Classifies_gpt_types(string? guid, PartitionKind expected) =>
        Assert.Equal(expected, StorageMapper.ClassifyGpt(guid));

    [Theory]
    [InlineData(0x07, PartitionKind.Basic)]
    [InlineData(0x0F, PartitionKind.Extended)]
    [InlineData(0x42, PartitionKind.DynamicMbr)]
    [InlineData(0x27, PartitionKind.Recovery)]
    [InlineData(0x83, PartitionKind.OemOrHidden)]
    public void Classifies_mbr_types(int type, PartitionKind expected) =>
        Assert.Equal(expected, StorageMapper.ClassifyMbr(type));

    [Theory]
    [InlineData((ushort)17, BusType.Nvme)]
    [InlineData((ushort)7, BusType.Usb)]
    [InlineData((ushort)11, BusType.Sata)]
    [InlineData((ushort)99, BusType.Other)]
    public void Maps_bus_types(ushort raw, BusType expected) => Assert.Equal(expected, StorageMapper.MapBus(raw));

    [Fact]
    public void Joins_partitions_to_volumes_by_volume_path_and_reads_bitlocker()
    {
        const string path = @"\\?\Volume{1111}\";
        var part = new RawPartition(0, 3, 1048576, 50UL * 1024 * 1024 * 1024, StorageMapper.GptBasicData, null,
            true, false, false, false, ["C:\\", path]);
        var vol = new RawVolume(path, 'c', "ntfs", null, 50UL * 1024 * 1024 * 1024, 10UL * 1024 * 1024 * 1024, 0);
        var enc = new RawEncryption(path, 1, false, 1);

        var disk = Assert.Single(StorageMapper.Map([RawDisk()], [part], [vol], [enc], encryptionKnown: true));
        var p = Assert.Single(disk.Partitions);
        Assert.Equal("C", p.Volume!.DriveLetter);
        Assert.Equal("NTFS", p.Volume.FileSystem);
        Assert.Equal(EncryptionState.Encrypted, p.Volume.Encryption);
        Assert.Equal(40 * ByteSize.GiB, p.Volume.UsedBytes);
        Assert.Equal(BusType.Nvme, disk.Bus);
    }

    [Fact]
    public void Encryption_is_unknown_when_windows_did_not_let_us_check()
    {
        const string path = @"\\?\Volume{2222}\";
        var part = new RawPartition(0, 1, 1048576, 1024UL * 1024 * 1024, StorageMapper.GptBasicData, null, false, false, false, false, [path]);
        var vol = new RawVolume(path, null, "NTFS", null, 1024UL * 1024 * 1024, 0, 0);
        var disk = StorageMapper.Map([RawDisk()], [part], [vol], [], encryptionKnown: false)[0];
        Assert.Equal(EncryptionState.Unknown, disk.Partitions[0].Volume!.Encryption);
        Assert.Null(disk.Partitions[0].Volume!.DriveLetter);
    }

    [Fact]
    public void Free_space_never_exceeds_volume_size()
    {
        const string path = @"\\?\Volume{3333}\";
        var part = new RawPartition(0, 1, 1048576, 1000, StorageMapper.GptBasicData, null, false, false, false, false, [path]);
        var vol = new RawVolume(path, 'D', "NTFS", null, 1000, 5000, 0);
        var v = StorageMapper.Map([RawDisk()], [part], [vol], [], true)[0].Partitions[0].Volume!;
        Assert.Equal(1000, v.FreeBytes);
        Assert.Equal(0, v.UsedBytes);
    }

    [Fact]
    public void Marks_logical_partitions_inside_extended_container()
    {
        var gib = 1024UL * 1024 * 1024;
        var parts = new[]
        {
            new RawPartition(0, 1, 1048576, 10 * gib, null, 0x07, false, false, true, false, []),
            new RawPartition(0, 2, 20 * gib, 40 * gib, null, 0x0F, false, false, false, false, []),
            new RawPartition(0, 5, 20 * gib + 1048576, 10 * gib, null, 0x07, false, false, false, false, []),
        };
        var disk = StorageMapper.Map([RawDisk(style: 1, bus: 11)], parts, [], [], true)[0];
        Assert.Equal(PartitionKind.Basic, disk.Partitions[0].Kind);
        Assert.Equal(PartitionKind.Logical, disk.Partitions.Single(p => p.PartitionNumber == 5).Kind);
    }

    [Theory]
    [InlineData("S4EWNX0R123456", "••••3456")]
    [InlineData("AB", "••••")]
    [InlineData(null, null)]
    public void Masks_serial_numbers(string? input, string? expected) =>
        Assert.Equal(expected, StorageMapper.MaskSerial(input));
}

public class SafetyTests
{
    private static DiskLayout Load(string fixture) => DiskLayoutBuilder.Build(FixtureInventory.LoadBuiltIn(fixture)[0]);

    [Fact]
    public void Dynamic_disk_blocks_every_action()
    {
        var layout = Load("dynamic-disk");
        Assert.True(layout.Disk.IsDynamic);
        Assert.Contains(SafetyAnalyzer.AnalyzeDisk(layout), n => n.Title == "Dynamic disk" && n.Severity == NoticeSeverity.Blocking);
        foreach (var r in layout.Regions.Where(r => r.Partition is not null))
            Assert.All(SafetyAnalyzer.AssessPartition(layout, r.Partition!), a => Assert.False(a.PassesBasicChecks));
    }

    [Fact]
    public void Efi_and_msr_are_never_resize_candidates()
    {
        var layout = Load("laptop-nvme-gpt");
        foreach (var p in layout.Disk.Partitions.Where(p => p.Kind is PartitionKind.EfiSystem or PartitionKind.MicrosoftReserved))
        {
            Assert.All(SafetyAnalyzer.AssessPartition(layout, p), a => Assert.False(a.PassesBasicChecks));
            Assert.Contains(SafetyAnalyzer.AnalyzePartition(p), n => n.Severity == NoticeSeverity.Blocking);
        }
    }

    [Fact]
    public void BitLocker_volume_gets_a_recovery_key_warning()
    {
        var layout = Load("laptop-nvme-gpt");
        Assert.Contains(SafetyAnalyzer.AnalyzeDisk(layout), n => n.Title == "BitLocker encrypted");
    }

    [Fact]
    public void Extend_needs_unallocated_space_directly_after_the_volume()
    {
        var layout = Load("desktop-hdd-mbr");
        var d = layout.Disk.Partitions.Single(p => p.Volume?.DriveLetter == "D");
        var e = layout.Disk.Partitions.Single(p => p.Volume?.DriveLetter == "E");

        Assert.False(SafetyAnalyzer.AssessPartition(layout, d).Single(a => a.Action == PlannedAction.ExtendVolume).PassesBasicChecks);
        // E: is exFAT — Windows cannot resize it in place even though free space follows it.
        var extendE = SafetyAnalyzer.AssessPartition(layout, e).Single(a => a.Action == PlannedAction.ExtendVolume);
        Assert.False(extendE.PassesBasicChecks);
        Assert.Contains(extendE.Reasons, r => r.Contains("NTFS and ReFS"));
        Assert.True(SafetyAnalyzer.AssessPartition(layout, d).Single(a => a.Action == PlannedAction.ShrinkVolume).PassesBasicChecks);
    }

    [Fact]
    public void Uninitialized_disk_warns_that_initializing_erases()
    {
        var layout = Load("new-disk-uninitialized");
        Assert.Contains(SafetyAnalyzer.AnalyzeDisk(layout), n => n.Title == "Disk not initialized");
        var region = Assert.Single(layout.Regions, r => r.Kind == RegionKind.Unallocated);
        Assert.False(SafetyAnalyzer.AssessUnallocated(layout, region).PassesBasicChecks);
    }

    [Fact]
    public void Health_warning_and_unknown_partition_are_flagged()
    {
        var notices = SafetyAnalyzer.AnalyzeDisk(Load("failing-hdd"));
        Assert.Contains(notices, n => n.Title == "Windows reports a health warning");
        Assert.Contains(notices, n => n.Title == "Unrecognised partition type");
    }

    [Fact]
    public void Unhealthy_disk_blocks_actions()
    {
        var layout = Load("desktop-hdd-mbr");
        var sick = DiskLayoutBuilder.Build(layout.Disk with { Health = ReportedHealth.Unhealthy });
        var region = sick.Regions.First(r => r.Kind == RegionKind.Unallocated);
        Assert.False(SafetyAnalyzer.AssessUnallocated(sick, region).PassesBasicChecks);
    }

    [Fact]
    public void Mbr_system_reserved_partition_is_protected_and_windows_volume_flagged()
    {
        var m = ByteSize.MiB;
        var sysRes = LayoutTests.Part(1, m, 50 * m, v: new Volume(null, "System Reserved", "NTFS", 50 * m, 18 * m, ReportedHealth.Healthy, EncryptionState.NotEncrypted))
            with { IsSystem = true, IsActive = true };
        var c = LayoutTests.Part(2, 51 * m, 100 * ByteSize.GiB, v: LayoutTests.Ntfs(100 * ByteSize.GiB, 60 * ByteSize.GiB, "C")) with { IsBoot = true };
        var layout = DiskLayoutBuilder.Build(LayoutTests.Disk(200 * ByteSize.GiB, PartitionStyle.Mbr, sysRes, c));

        Assert.Equal("System Reserved", sysRes.DisplayName);
        Assert.True(sysRes.IsStartupPartition);
        Assert.All(SafetyAnalyzer.AssessPartition(layout, sysRes), a => Assert.False(a.PassesBasicChecks));
        Assert.Contains(SafetyAnalyzer.AnalyzePartition(sysRes), n => n.Title == "System partition" && n.Severity == NoticeSeverity.Blocking);

        Assert.Contains(SafetyAnalyzer.AnalyzePartition(c), n => n.Title == "Windows is installed here");
        Assert.True(SafetyAnalyzer.AssessPartition(layout, c).Single(a => a.Action == PlannedAction.ShrinkVolume).PassesBasicChecks);
    }

    [Fact]
    public void Mbr_disk_with_four_primaries_cannot_take_another()
    {
        var g = ByteSize.GiB;
        var disk = LayoutTests.Disk(100 * g, PartitionStyle.Mbr,
            LayoutTests.Part(1, ByteSize.MiB, g), LayoutTests.Part(2, 2 * g, g),
            LayoutTests.Part(3, 4 * g, g), LayoutTests.Part(4, 6 * g, g));
        var layout = DiskLayoutBuilder.Build(disk);
        var region = layout.Regions.Last(r => r.Kind == RegionKind.Unallocated);
        Assert.Contains(SafetyAnalyzer.AssessUnallocated(layout, region).Reasons, r => r.Contains("four primary"));
    }
}

public class ReportAndLogTests
{
    [Fact]
    public async Task Report_marks_simulated_data_and_masks_serials()
    {
        var snap = await FixtureInventory.DemoAll().GetSnapshotAsync(CancellationToken.None);
        var text = ReportWriter.Write(snap, "0.1.0");
        Assert.Contains("SIMULATED DATA", text);
        Assert.DoesNotContain("DEMO-0000-0000-A1B2", text);
        Assert.Contains("••••A1B2", text);
        Assert.Contains(SafetyAnalyzer.ReadOnlyStatement, text);
    }

    [Fact]
    public void Log_writes_timestamped_lines_to_a_daily_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shadowdisk-test-" + Guid.NewGuid());
        try
        {
            var time = new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);
            var log = new OperationLog(dir, () => time);
            LogEntry? seen = null;
            log.EntryAdded += e => seen = e;
            log.Info("Inventory refreshed: 3 disks.");

            Assert.NotNull(seen);
            var line = File.ReadAllText(Path.Combine(dir, "shadowdisk-2026-11-12.log"));
            Assert.Contains("2026-11-12 09:30:00 [Info] Inventory refreshed: 3 disks.", line);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Log_without_directory_keeps_entries_in_memory_only()
    {
        var log = new OperationLog(null);
        log.Warning("x");
        Assert.Single(log.Entries);
        Assert.Null(log.CurrentFilePath);
    }
}
