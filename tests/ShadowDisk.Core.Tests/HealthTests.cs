using ShadowDisk.Core;
using ShadowDisk.Core.Health;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Tests;

/// <summary>A disk made of memory. Chosen blocks fail or are slow; every read is recorded, so tests can check nothing is re-read.</summary>
internal sealed class FakeRawDisk(long size, int sector = 512) : IRawDiskReader
{
    public HashSet<long> BadOffsets { get; } = [];
    public HashSet<long> SlowOffsets { get; } = [];
    public List<long> Reads { get; } = [];
    public Action<long>? OnRead { get; set; }
    public long SizeBytes => size;
    public int SectorSize => sector;

    public string? Read(long offset, int length)
    {
        Reads.Add(offset);
        OnRead?.Invoke(offset);
        if (SlowOffsets.Contains(offset)) Thread.Sleep(30);
        return BadOffsets.Contains(offset) ? "Data error (cyclic redundancy check)." : null;
    }

    public void Dispose() { }
}

public class HealthTests
{
    private const long MiB = ByteSize.MiB;
    private static readonly ScanOptions Fast = new() { BlockBytes = (int)MiB, SlowMilliseconds = 20, ProgressInterval = TimeSpan.Zero };

    private static DriveHealthReport Report(ReportedHealth health = ReportedHealth.Healthy, long? uncorrected = 0, int? wear = null, int? temp = 35,
        DriveMedia media = DriveMedia.Hdd) =>
        new(0, "Disk", media, health, ["OK"], temp, 45, 12000, 10, uncorrected, 0, 0, wear, 900, 5000);

    [Fact]
    public void A_healthy_drive_reads_as_good_and_never_claims_a_scan()
    {
        var a = HealthAssessor.Assess(Report());
        Assert.Equal(HealthVerdict.Good, a.Verdict);
        Assert.Contains(a.Findings, f => f.Contains("not a surface scan"));
    }

    [Fact]
    public void Uncorrected_errors_or_an_unhealthy_drive_need_attention()
    {
        Assert.Equal(HealthVerdict.Concern, HealthAssessor.Assess(Report(uncorrected: 3)).Verdict);
        Assert.Equal(HealthVerdict.Concern, HealthAssessor.Assess(Report(ReportedHealth.Unhealthy)).Verdict);
        Assert.Equal(HealthVerdict.Concern, HealthAssessor.Assess(Report(wear: 95, media: DriveMedia.Ssd)).Verdict);
        Assert.Equal(HealthVerdict.Watch, HealthAssessor.Assess(Report(temp: 60)).Verdict);
    }

    [Fact]
    public void A_drive_without_counters_is_not_treated_as_a_problem()
    {
        var bare = new DriveHealthReport(0, "RAID volume", DriveMedia.Unknown, ReportedHealth.Healthy, ["OK"],
            null, null, null, null, null, null, null, null, null, null);
        var a = HealthAssessor.Assess(bare);
        Assert.Equal(HealthVerdict.Good, a.Verdict);
        Assert.Contains(a.Findings, f => f.Contains("RAID"));
        var unknown = bare with { WindowsHealth = ReportedHealth.Unknown };
        Assert.Equal(HealthVerdict.Unknown, HealthAssessor.Assess(unknown).Verdict);
    }

    [Fact]
    public void Scan_reads_every_block_once_and_records_problems_without_re_reading()
    {
        var disk = new FakeRawDisk(20 * MiB + 4096);
        disk.BadOffsets.Add(5 * MiB);
        disk.SlowOffsets.Add(9 * MiB);
        var map = new ScanMap(disk.SizeBytes, 21);
        var problems = new List<ScanProblem>();

        var result = SurfaceScanner.Scan(disk, 0, map, problems, Fast, null, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(disk.SizeBytes, result.ScannedUpToBytes);
        Assert.Equal(21, disk.Reads.Count);                       // 20 full blocks + the 4 KiB tail
        Assert.Equal(disk.Reads.Count, disk.Reads.Distinct().Count());   // nothing read twice
        Assert.Contains(result.Problems, p => p.Kind == ScanProblemKind.Unreadable && p.OffsetBytes == 5 * MiB);
        Assert.Contains(result.Problems, p => p.Kind == ScanProblemKind.Slow && p.OffsetBytes == 9 * MiB);
        Assert.Equal(BlockState.Unreadable, map[5]);
        Assert.Equal(BlockState.Slow, map[9]);
        Assert.Equal(BlockState.Good, map[0]);
    }

    [Fact]
    public void Scan_can_be_cancelled_and_continued_where_it_stopped()
    {
        var disk = new FakeRawDisk(10 * MiB);
        disk.BadOffsets.Add(2 * MiB);
        using var cts = new CancellationTokenSource();
        disk.OnRead = o => { if (o == 4 * MiB) cts.Cancel(); };
        var map = new ScanMap(disk.SizeBytes, 10);
        var problems = new List<ScanProblem>();

        var first = SurfaceScanner.Scan(disk, 0, map, problems, Fast, null, cts.Token);
        Assert.Equal(ScanStatus.Cancelled, first.Status);
        Assert.Equal(5 * MiB, first.ScannedUpToBytes);

        disk.OnRead = null;
        var second = SurfaceScanner.Scan(disk, first.ScannedUpToBytes, map, problems, Fast, null, CancellationToken.None);
        Assert.Equal(ScanStatus.Completed, second.Status);
        Assert.Equal(10, disk.Reads.Count);
        Assert.Single(second.Problems);                           // the earlier problem is kept
    }

    [Fact]
    public void Scan_stops_by_itself_when_many_areas_are_unreadable()
    {
        var disk = new FakeRawDisk(100 * MiB);
        for (var i = 10; i < 60; i++) disk.BadOffsets.Add(i * MiB);
        var options = new ScanOptions { BlockBytes = (int)MiB, StopAfterUnreadable = 5, ProgressInterval = TimeSpan.Zero };
        var result = SurfaceScanner.Scan(disk, 0, new ScanMap(disk.SizeBytes), [], options, null, CancellationToken.None);
        Assert.Equal(ScanStatus.StoppedTooManyErrors, result.Status);
        Assert.Equal(15, disk.Reads.Count);
        Assert.Contains("Copy your important files", result.Message);
    }

    [Fact]
    public void Problems_are_described_with_the_partition_they_are_in()
    {
        var disk = new PhysicalDisk(1, "USB", null, 100 * MiB, 512, 512, PartitionStyle.Mbr, BusType.Usb, false, false, false, false,
            ReportedHealth.Healthy,
            [new Partition(1, 1, MiB, 50 * MiB, PartitionKind.Basic, null, 7, false, false, false, false,
                new Volume("G", "Photos", "NTFS", 50 * MiB, 10 * MiB, ReportedHealth.Healthy, EncryptionState.NotEncrypted))]);
        Assert.Equal("in G:", SurfaceScanner.WhereIs(disk, 20 * MiB));
        Assert.Equal("not in any partition", SurfaceScanner.WhereIs(disk, 80 * MiB));
        var text = SurfaceScanner.Describe(disk, new ScanResult(ScanStatus.Completed, 100 * MiB, 100 * MiB, TimeSpan.FromSeconds(5),
            [new ScanProblem(20 * MiB, MiB, ScanProblemKind.Unreadable, 3, "CRC")], "Finished."));
        Assert.Contains("UNREADABLE", text);
        Assert.Contains("in G:", text);
    }
}
