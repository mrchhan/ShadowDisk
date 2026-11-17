using ShadowDisk.Core;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Moving;
using ShadowDisk.Core.Operations;

namespace ShadowDisk.Core.Tests;

/// <summary>A disk made of memory. Can "lose power" after a number of writes, half-way through a write.</summary>
internal sealed class MemoryDevice(byte[] disk, int crashAfterWrites = -1) : IMoveDevice
{
    private int _writes;
    public int SectorSize => 512;
    public int Writes => _writes;

    public void Read(long diskOffset, Span<byte> buffer) => disk.AsSpan((int)diskOffset, buffer.Length).CopyTo(buffer);

    public void Write(long diskOffset, ReadOnlySpan<byte> data)
    {
        if (crashAfterWrites >= 0 && _writes == crashAfterWrites)
        {
            data[..(data.Length / 2)].CopyTo(disk.AsSpan((int)diskOffset));   // half of the piece reaches the disk…
            throw new IOException("Simulated power cut.");                     // …then the power goes
        }
        data.CopyTo(disk.AsSpan((int)diskOffset));
        _writes++;
    }

    public void Flush() { }
    public void Dispose() { }
}

internal sealed class FakeMoveHost(FakeInventory inventory, byte[] disk, IReadOnlyList<ByteRange> used, int crashAfterWrites = -1) : IMoveHost
{
    public bool Hidden { get; private set; }
    public bool LockFails { get; init; }
    public MemoryDevice? Device { get; private set; }

    public Task<(IReadOnlyList<ByteRange>? Used, string? Error)> TakeOfflineAsync(MoveJournal j, bool readUsedMap, CancellationToken ct) =>
        Task.FromResult<(IReadOnlyList<ByteRange>?, string?)>(LockFails
            ? (null, "G: is in use by another program, so it could not be locked.")
            : (readUsedMap ? used : [], null));

    public Task<string?> CheckAsync(MoveJournal j, CancellationToken ct) =>
        Task.FromResult(LockFails ? "G: is in use by another program, so it could not be locked." : null);

    public Task<BackendResult> HideAsync(MoveJournal j, MoveJournalStore store, CancellationToken ct)
    {
        j.OriginalGptType = StorageMapper.GptBasicData;
        store.Save(j);
        Hidden = true;
        return Task.FromResult(new BackendResult(true, null));
    }

    public IMoveDevice OpenDevice(MoveJournal j) => Device = new MemoryDevice(disk, crashAfterWrites);

    public Task<BackendResult> FinishAsync(MoveJournal j, CancellationToken ct)
    {
        var d = inventory.Disks[0];
        inventory.Disks[0] = d with { Partitions = d.Partitions.Select(p => p.OffsetBytes == j.OldOffset ? p with { OffsetBytes = j.NewOffset } : p).OrderBy(p => p.OffsetBytes).ToList() };
        Hidden = false;
        return Task.FromResult(new BackendResult(true, null));
    }

    public void Release() { }
}

public sealed class MoveTests : IDisposable
{
    private const long MiB = ByteSize.MiB;
    private const string PracticeFolder = @"C:\Users\Test\AppData\Local\ShadowDisk\practice";
    private readonly string _journals = Path.Combine(Path.GetTempPath(), "shadowdisk-move-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_journals, true); } catch (IOException) { }
    }

    private static Volume Vol(string letter, long size, long used, string fs = "NTFS", EncryptionState enc = EncryptionState.NotEncrypted) =>
        new(letter, null, fs, size, size - used, ReportedHealth.Healthy, enc);

    /// <summary>64 MiB GPT practice disk: A: 1–9 MiB | free 9–25 | G: 25–41 MiB (16 MiB) | free 41–63.</summary>
    private static PhysicalDisk Disk(Volume? g = null) =>
        new(7, "Msft Virtual Disk", null, 64 * MiB, 512, 512, PartitionStyle.Gpt, BusType.FileBackedVirtual,
            false, false, false, false, ReportedHealth.Healthy,
            [
                new Partition(7, 1, MiB, 8 * MiB, PartitionKind.Basic, StorageMapper.GptBasicData, null, false, false, false, false, Vol("A", 8 * MiB, MiB)),
                new Partition(7, 2, 25 * MiB, 16 * MiB, PartitionKind.Basic, StorageMapper.GptBasicData, null, false, false, false, false, g ?? Vol("G", 16 * MiB, 4 * MiB)),
            ],
            Location: PracticeFolder + @"\practice-1.vhd", UniqueId: "VHD-1");

    private static readonly ByteRange[] Used = [new(0, 4096), new(MiB, 2 * MiB + MiB / 2), new(10 * MiB + 512, 2 * MiB - 512)];

    /// <summary>Fills G:'s used parts with recognisable bytes and an NTFS boot sector (and backup) pointing at its start.</summary>
    private static byte[] MakeDisk(long gOffset = 25 * MiB, long gSize = 16 * MiB)
    {
        var disk = new byte[64 * MiB];
        var rng = new Random(42);
        foreach (var r in Used) rng.NextBytes(disk.AsSpan((int)(gOffset + r.Offset), (int)r.Length));
        foreach (var at in new[] { gOffset, gOffset + gSize - 512 })
        {
            "NTFS"u8.CopyTo(disk.AsSpan((int)at + 3));
            BitConverter.TryWriteBytes(disk.AsSpan((int)at + 0x1C, 4), (uint)(gOffset / 512));
        }
        return disk;
    }

    private static void AssertMoved(byte[] original, byte[] disk, long from, long to, long size)
    {
        foreach (var r in MoveEngine.Normalize(Used, size, 512))
        {
            var expected = original.AsSpan((int)(from + r.Offset), (int)r.Length).ToArray();
            var actual = disk.AsSpan((int)(to + r.Offset), (int)r.Length).ToArray();
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] == actual[i]) continue;
                // The boot sectors' "hidden sectors" field is expected to change; everything else must match.
                var inBoot = r.Offset + i is >= 0x1C and < 0x20 || r.Offset + i - (size - 512) is >= 0x1C and < 0x20;
                Assert.True(inBoot, $"byte {r.Offset + i} of the partition differs");
            }
        }
        Assert.Equal((uint)(to / 512), BitConverter.ToUInt32(disk, (int)to + 0x1C));
        Assert.Equal((uint)(to / 512), BitConverter.ToUInt32(disk, (int)(to + size - 512) + 0x1C));
    }

    private (FakeInventory Inventory, PartitionMover Mover, FakeMoveHost Host, MoveJournalStore Store) Setup(byte[] bytes, int crashAfter = -1, PhysicalDisk? disk = null)
    {
        var inventory = new FakeInventory([disk ?? Disk()]);
        var host = new FakeMoveHost(inventory, bytes, Used, crashAfter);
        var store = new MoveJournalStore(_journals);
        return (inventory, new PartitionMover(inventory, host, new OperationPolicy(PracticeFolder), new OperationLog(null), store), host, store);
    }

    private static MoveOrder Order(PhysicalDisk disk, long newOffset) =>
        new(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk), 2, 25 * MiB, 16 * MiB, newOffset);

    [Theory]
    [InlineData(9)]    // left by 16 MiB (pieces of 8 MiB)
    [InlineData(24)]   // left by 1 MiB: pieces of 1 MiB, overlapping its own old place
    [InlineData(26)]   // right by 1 MiB
    [InlineData(47)]   // right to the end of the free space
    public async Task A_partition_moves_and_every_used_byte_arrives(int newMiB)
    {
        var bytes = MakeDisk();
        var original = bytes.ToArray();
        var (inventory, mover, host, store) = Setup(bytes);

        var outcome = await mover.MoveAsync(Order(Disk(), newMiB * MiB), null, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, outcome.Status);
        AssertMoved(original, bytes, 25 * MiB, newMiB * MiB, 16 * MiB);
        Assert.Contains(inventory.Disks[0].Partitions, p => p.OffsetBytes == newMiB * MiB && p.Volume?.DriveLetter == "G");
        Assert.False(host.Hidden);
        Assert.Empty(store.LoadAll());      // the journal is removed after success
        Assert.Equal(original.AsSpan((int)MiB, (int)(8 * MiB)).ToArray(), bytes.AsSpan((int)MiB, (int)(8 * MiB)).ToArray());   // A: untouched
    }

    [Theory]
    [InlineData(24, 3)]    // overlapping small move, power cut after 3 pieces
    [InlineData(9, 0)]     // power cut during the very first piece
    [InlineData(26, 2)]
    public async Task A_power_cut_half_way_can_be_resumed_without_losing_a_byte(int newMiB, int crashAfterWrites)
    {
        var bytes = MakeDisk();
        var original = bytes.ToArray();
        var (inventory, mover, host, store) = Setup(bytes, crashAfterWrites);

        var stopped = await mover.MoveAsync(Order(Disk(), newMiB * MiB), null, CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, stopped.Status);   // stops at once, never retries
        Assert.Contains("resumed", stopped.Message);
        Assert.True(host.Hidden);                               // still hidden from Windows: never mounted half-moved
        var journal = Assert.Single(store.LoadAll());
        Assert.Equal(MovePhase.Copying, journal.Phase);

        // "Restart": a new host without the fault, and the journal from disk.
        var host2 = new FakeMoveHost(inventory, bytes, Used);
        var mover2 = new PartitionMover(inventory, host2, new OperationPolicy(PracticeFolder), new OperationLog(null), store);
        var resumed = await mover2.ResumeAsync(journal, null, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, resumed.Status);
        AssertMoved(original, bytes, 25 * MiB, newMiB * MiB, 16 * MiB);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public async Task Pausing_keeps_the_journal_and_resume_finishes()
    {
        var bytes = MakeDisk();
        var original = bytes.ToArray();
        var (inventory, mover, _, store) = Setup(bytes);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<MoveProgress>(_ => cts.Cancel());

        var paused = await mover.MoveAsync(Order(Disk(), 24 * MiB), progress, cts.Token);
        Assert.Equal(OperationStatus.Cancelled, paused.Status);
        var journal = Assert.Single(store.LoadAll());

        var host2 = new FakeMoveHost(inventory, bytes, Used);
        var resumed = await new PartitionMover(inventory, host2, new OperationPolicy(PracticeFolder), new OperationLog(null), store)
            .ResumeAsync(journal, null, CancellationToken.None);
        Assert.Equal(OperationStatus.Succeeded, resumed.Status);
        AssertMoved(original, bytes, 25 * MiB, 24 * MiB, 16 * MiB);
    }

    [Fact]
    public async Task A_volume_in_use_is_not_touched()
    {
        var bytes = MakeDisk();
        var original = bytes.ToArray();
        var inventory = new FakeInventory([Disk()]);
        var host = new FakeMoveHost(inventory, bytes, Used) { LockFails = true };
        var store = new MoveJournalStore(_journals);
        var mover = new PartitionMover(inventory, host, new OperationPolicy(PracticeFolder), new OperationLog(null), store);

        var outcome = await mover.MoveAsync(Order(Disk(), 9 * MiB), null, CancellationToken.None);
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Contains("in use", outcome.Message);
        Assert.Equal(original, bytes);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void Locked_partitions_and_limits()
    {
        var disk = Disk();
        var layout = DiskLayoutBuilder.Build(disk);
        var g = disk.Partitions[1];
        var range = MovePlanner.Range(layout, g);
        Assert.True(range.CanMove);
        Assert.Equal(9 * MiB, range.MinOffset);                        // right after A:
        Assert.Equal(64 * MiB - MiB - 16 * MiB, range.MaxOffset);      // up to the GPT tail reserve

        var windows = disk with { Partitions = [disk.Partitions[0], g with { IsBoot = true }] };
        Assert.Contains("locked in place", MovePlanner.Range(DiskLayoutBuilder.Build(windows), windows.Partitions[1]).Locked);
        var bitlocker = Disk(Vol("G", 16 * MiB, MiB, enc: EncryptionState.Encrypted));
        Assert.Contains("BitLocker", MovePlanner.Range(DiskLayoutBuilder.Build(bitlocker), bitlocker.Partitions[1]).Locked);
        var fat = Disk(Vol("G", 16 * MiB, MiB, fs: "FAT32"));
        Assert.Contains("NTFS only", MovePlanner.Range(DiskLayoutBuilder.Build(fat), fat.Partitions[1]).Locked);
    }

    [Fact]
    public void Pieces_never_overlap_their_own_original()
    {
        var ranges = MoveEngine.Normalize(Used, 16 * MiB, 512);
        foreach (var distance in new[] { MiB, 3 * MiB, 16 * MiB })
        {
            var size = MoveEngine.ChunkSize(distance, 512);
            Assert.True(size <= distance);
            var left = MoveEngine.Chunks(ranges, size, towardsStart: true);
            Assert.True(left.Zip(left.Skip(1)).All(p => p.First.Offset < p.Second.Offset));      // first to last
            var right = MoveEngine.Chunks(ranges, size, towardsStart: false);
            Assert.True(right.Zip(right.Skip(1)).All(p => p.First.Offset > p.Second.Offset));    // last to first
        }
    }

    [Fact]
    public void Estimates_favour_sector_moves()
    {
        var sector = MoveEstimate.SectorMove(60L * 1024 * MiB, 70);
        var copy = MoveEstimate.DataCopy(60L * 1024 * MiB, 70);
        Assert.True(copy > sector * 1.5);
        Assert.StartsWith("about", MoveEstimate.Format(sector));
    }
}

/// <summary>Reports progress on the calling thread (Progress&lt;T&gt; would post it later).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public sealed class SectorRebalanceTests : IDisposable
{
    private const long MiB = ByteSize.MiB;
    private const string PracticeFolder = @"C:\Users\Test\AppData\Local\ShadowDisk\practice";
    private readonly string _journals = Path.Combine(Path.GetTempPath(), "shadowdisk-sector-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_journals, true); } catch (IOException) { }
    }

    private static Volume Vol(string letter, long size, long used) => new(letter, null, "NTFS", size, size - used, ReportedHealth.Healthy, EncryptionState.NotEncrypted);

    /// <summary>256 MiB practice disk: A: 1–65 MiB | G: 65–129 MiB | free 129–255.</summary>
    private static PhysicalDisk Disk() =>
        new(7, "Msft Virtual Disk", null, 256 * MiB, 512, 512, PartitionStyle.Gpt, BusType.FileBackedVirtual,
            false, false, false, false, ReportedHealth.Healthy,
            [
                new Partition(7, 1, MiB, 64 * MiB, PartitionKind.Basic, StorageMapper.GptBasicData, null, false, false, false, false, Vol("A", 64 * MiB, MiB)),
                new Partition(7, 2, 65 * MiB, 64 * MiB, PartitionKind.Basic, StorageMapper.GptBasicData, null, false, false, false, false, Vol("G", 64 * MiB, 8 * MiB)),
            ],
            Location: PracticeFolder + @"\practice-1.vhd", UniqueId: "VHD-1");

    private static readonly ByteRange[] Used = [new(0, 4096), new(MiB, 2 * MiB)];

    private (FakeInventory Inventory, SectorRebalanceRunner Runner, FakeDiskOperations Ops) Setup(bool lockFails = false)
    {
        var inventory = new FakeInventory([Disk()]);
        var policy = new OperationPolicy(PracticeFolder);
        var log = new OperationLog(null);
        var ops = new FakeDiskOperations(inventory);
        var host = new FakeMoveHost(inventory, new byte[256 * MiB], Used) { LockFails = lockFails };
        var runner = new OperationRunner(inventory, ops, policy, log);
        var mover = new PartitionMover(inventory, host, policy, log, new MoveJournalStore(_journals));
        return (inventory, new SectorRebalanceRunner(inventory, runner, mover, log), ops);
    }

    private static (long Offset, long Size)[] Shape(FakeInventory inventory) =>
        inventory.Disks[0].Partitions.Select(p => (p.OffsetBytes, p.SizeBytes)).ToArray();

    [Fact]
    public async Task Dragging_the_divider_right_gives_the_left_drive_space_without_copying_files()
    {
        var (inventory, runner, ops) = Setup();
        var disk = Disk();
        var plan = SectorPlanner.ForBoundary(DiskLayoutBuilder.Build(disk), disk.Partitions[0], disk.Partitions[1], 85 * MiB);
        Assert.True(plan.IsPossible);
        Assert.Equal([SectorStepKind.Shrink, SectorStepKind.Move, SectorStepKind.Extend], plan.Steps.Select(s => s.Kind).ToArray());

        var outcome = await runner.RunAsync(7, "VHD-1", plan, null, CancellationToken.None);
        Assert.True(outcome.Status == OperationStatus.Succeeded, outcome.Message + " | " + string.Join(" | ", outcome.Steps));
        Assert.Equal([(MiB, 84 * MiB), (85 * MiB, 44 * MiB)], Shape(inventory));
        Assert.Equal(2, ops.ResizeCalls);
    }

    [Fact]
    public async Task Dragging_the_divider_left_gives_the_right_drive_space()
    {
        var (inventory, runner, _) = Setup();
        var disk = Disk();
        var plan = SectorPlanner.ForBoundary(DiskLayoutBuilder.Build(disk), disk.Partitions[0], disk.Partitions[1], 41 * MiB);
        Assert.True(plan.IsPossible, string.Join(" ", plan.Problems));
        var outcome = await runner.RunAsync(7, "VHD-1", plan, null, CancellationToken.None);
        Assert.Equal(OperationStatus.Succeeded, outcome.Status);
        Assert.Equal([(MiB, 40 * MiB), (41 * MiB, 88 * MiB)], Shape(inventory));
    }

    [Fact]
    public async Task A_drive_can_take_in_the_free_space_in_front_of_it()
    {
        var (inventory, runner, _) = Setup();
        // Free space in front of G: make A smaller first.
        inventory.Disks[0] = inventory.Disks[0] with { Partitions = [inventory.Disks[0].Partitions[0] with { SizeBytes = 32 * MiB, Volume = Vol("A", 32 * MiB, MiB) }, inventory.Disks[0].Partitions[1]] };
        var disk = inventory.Disks[0];
        var plan = SectorPlanner.ForMove(DiskLayoutBuilder.Build(disk), disk.Partitions[1], 33 * MiB, takeInFreedSpace: true);
        Assert.True(plan.IsPossible);
        Assert.Contains("takes in", plan.Summary);
        var outcome = await runner.RunAsync(7, "VHD-1", plan, null, CancellationToken.None);
        Assert.Equal(OperationStatus.Succeeded, outcome.Status);
        Assert.Equal([(MiB, 32 * MiB), (33 * MiB, 96 * MiB)], Shape(inventory));
    }

    [Fact]
    public async Task A_failed_step_stops_and_says_what_is_done()
    {
        var (inventory, runner, ops) = Setup();
        var disk = Disk();
        var plan = SectorPlanner.ForBoundary(DiskLayoutBuilder.Build(disk), disk.Partitions[0], disk.Partitions[1], 85 * MiB);
        var steps = 0;
        inventory.BeforeRead = () => { if (++steps > 6) ops.FailCompletely = true; };   // the extend at the end fails
        var outcome = await runner.RunAsync(7, "VHD-1", plan, null, CancellationToken.None);
        Assert.Equal(OperationStatus.PartiallyApplied, outcome.Status);
        Assert.Contains("are done", outcome.Message);
        Assert.Equal((85 * MiB, 44 * MiB), Shape(inventory)[1]);   // the move stays done
    }

    [Fact]
    public async Task If_the_drive_that_moves_cannot_be_locked_nothing_is_shrunk()
    {
        var (inventory, runner, ops) = Setup(lockFails: true);
        var disk = Disk();
        foreach (var boundary in new[] { 85 * MiB, 41 * MiB })
        {
            var plan = SectorPlanner.ForBoundary(DiskLayoutBuilder.Build(disk), disk.Partitions[0], disk.Partitions[1], boundary);
            var outcome = await runner.RunAsync(7, "VHD-1", plan, null, CancellationToken.None);
            Assert.Equal(OperationStatus.Blocked, outcome.Status);
            Assert.Contains("Nothing was changed", outcome.Message);
        }
        Assert.Equal(0, ops.ResizeCalls);
        Assert.Equal([(MiB, 64 * MiB), (65 * MiB, 64 * MiB)], Shape(inventory));
    }

    [Fact]
    public void The_windows_drive_and_too_much_shrinking_are_refused()
    {
        var disk = Disk();
        var windows = disk with { Partitions = [disk.Partitions[0] with { IsBoot = true }, disk.Partitions[1]] };
        var layout = DiskLayoutBuilder.Build(windows);
        Assert.Contains("only get bigger", SectorPlanner.ForBoundary(layout, windows.Partitions[0], windows.Partitions[1], 41 * MiB).Problems[0]);
        Assert.True(SectorPlanner.ForBoundary(layout, windows.Partitions[0], windows.Partitions[1], 85 * MiB).IsPossible);   // C: may grow
        var lockedRight = disk with { Partitions = [disk.Partitions[0], disk.Partitions[1] with { IsBoot = true }] };
        Assert.Contains("locked", SectorPlanner.ForBoundary(DiskLayoutBuilder.Build(lockedRight), lockedRight.Partitions[0], lockedRight.Partitions[1], 85 * MiB).Problems[0]);
        Assert.Contains("can't get smaller", SectorPlanner.ForBoundary(DiskLayoutBuilder.Build(disk), disk.Partitions[0], disk.Partitions[1], 128 * MiB).Problems[0]);
    }
}

public sealed class NtfsBitmapTests
{
    /// <summary>A tiny fake NTFS volume: 512-byte clusters, $MFT at cluster 16, $Bitmap stored in two pieces (the second before the first).</summary>
    private static byte[] MakeVolume(long[] usedClusters, bool breakFixup = false)
    {
        const int Size = 8 * 1024 * 1024, Cluster = 512, MftLcn = 16, RecordSize = 1024;
        var v = new byte[Size];
        "NTFS"u8.CopyTo(v.AsSpan(3));
        BitConverter.TryWriteBytes(v.AsSpan(0x0B), (ushort)512);
        v[0x0D] = 1;                                                   // 1 sector per cluster
        BitConverter.TryWriteBytes(v.AsSpan(0x28), (long)(Size / 512 - 1));
        BitConverter.TryWriteBytes(v.AsSpan(0x30), (long)MftLcn);
        v[0x40] = 0xF6;                                                // records of 2^10 bytes

        var rec = new byte[RecordSize];
        "FILE"u8.CopyTo(rec);
        BitConverter.TryWriteBytes(rec.AsSpan(4), (ushort)0x30);      // update sequence at 0x30
        BitConverter.TryWriteBytes(rec.AsSpan(6), (ushort)3);
        BitConverter.TryWriteBytes(rec.AsSpan(0x14), (ushort)0x38);   // first attribute
        var a = 0x38;
        BitConverter.TryWriteBytes(rec.AsSpan(a), 0x80u);
        BitConverter.TryWriteBytes(rec.AsSpan(a + 4), 0x50u);
        rec[a + 8] = 1;                                                // non-resident, no name
        BitConverter.TryWriteBytes(rec.AsSpan(a + 0x20), (ushort)0x40);
        const int BitmapBytes = 16383 / 8 + 1;
        BitConverter.TryWriteBytes(rec.AsSpan(a + 0x30), (long)BitmapBytes);
        // runs: 2 clusters at LCN 300, then 3 clusters at LCN 250 (offset -50)
        byte[] runs = [0x21, 2, 0x2C, 0x01, 0x11, 3, 0xCE, 0];
        runs.CopyTo(rec.AsSpan(a + 0x40));
        BitConverter.TryWriteBytes(rec.AsSpan(a + 0x50), 0xFFFFFFFFu);
        // fixups
        BitConverter.TryWriteBytes(rec.AsSpan(0x30), (ushort)0xABCD);
        foreach (var i in new[] { 1, 2 })
        {
            rec[0x30 + i * 2] = rec[i * 512 - 2];
            rec[0x30 + i * 2 + 1] = rec[i * 512 - 1];
            BitConverter.TryWriteBytes(rec.AsSpan(i * 512 - 2), (ushort)(breakFixup && i == 2 ? 0x1111 : 0xABCD));
        }
        rec.CopyTo(v.AsSpan(MftLcn * Cluster + 6 * RecordSize));

        var bitmap = new byte[5 * Cluster];
        foreach (var c in usedClusters) bitmap[c / 8] |= (byte)(1 << (int)(c % 8));
        bitmap.AsSpan(0, 2 * Cluster).CopyTo(v.AsSpan(300 * Cluster));
        bitmap.AsSpan(2 * Cluster, 3 * Cluster).CopyTo(v.AsSpan(250 * Cluster));
        return v;
    }

    [Fact]
    public void Reads_used_clusters_from_the_disk_across_split_pieces()
    {
        long[] used = [0, 1, 2, 3, 16, 17, 18, 5000, 5001, 9000, 16382];   // 9000 lives in the second piece
        var v = MakeVolume(used);
        var (ranges, error) = NtfsBitmap.Read((o, b) => v.AsSpan((int)o, b.Length).CopyTo(b), 512, v.Length);
        Assert.Null(error);
        Assert.Equal(
            [new ByteRange(0, 4 * 512), new ByteRange(16 * 512, 3 * 512), new ByteRange(5000 * 512, 2 * 512), new ByteRange(9000 * 512, 512), new ByteRange(16382 * 512, 512)],
            ranges!.ToArray());
    }

    [Fact]
    public void A_damaged_record_or_a_non_ntfs_volume_is_refused()
    {
        var bad = MakeVolume([0], breakFixup: true);
        Assert.Contains("damaged", NtfsBitmap.Read((o, b) => bad.AsSpan((int)o, b.Length).CopyTo(b), 512, bad.Length).Error);
        var fat = new byte[1024 * 1024];
        Assert.Contains("NTFS", NtfsBitmap.Read((o, b) => fat.AsSpan((int)o, b.Length).CopyTo(b), 512, fat.Length).Error);
    }
}
