using ShadowDisk.Core;
using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.Core.Tests;

/// <summary>Inventory whose disks the fake backend can change, so the runner's read-back can be checked.</summary>
internal sealed class FakeInventory(List<PhysicalDisk> disks) : IStorageInventory
{
    public List<PhysicalDisk> Disks { get; } = disks;
    public int Reads { get; private set; }
    public Action? BeforeRead { get; set; }
    public bool IsSimulated => true;
    public string SourceDescription => "fake";

    public Task<InventorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeRead?.Invoke();
        Reads++;
        return Task.FromResult(new InventorySnapshot(DateTimeOffset.Now, true, "fake", Disks.ToList(), []));
    }
}

/// <summary>Records every call. Can simulate success, a failure, or a partial failure (partition made, format failed).</summary>
internal sealed class FakeDiskOperations(FakeInventory inventory) : IDiskOperations
{
    public int Calls { get; private set; }
    public bool FailCompletely { get; set; }
    public bool FailFormat { get; set; }
    public bool Throw { get; set; }

    public int ResizeCalls { get; private set; }
    public long? ResizeTo { get; set; }   // simulate Windows resizing to a different size than asked

    public Task<ResizeBackendResult> ResizePartitionAsync(PhysicalDisk disk, int partitionNumber, long offsetBytes, long targetSizeBytes)
    {
        ResizeCalls++;
        if (Throw) throw new InvalidOperationException("simulated crash");
        if (FailCompletely) return Task.FromResult(new ResizeBackendResult(false, "The extent specified is not valid"));
        var size = ResizeTo ?? targetSizeBytes;
        var index = inventory.Disks.FindIndex(d => d.Number == disk.Number);
        inventory.Disks[index] = disk with
        {
            Partitions = disk.Partitions.Select(p => p.PartitionNumber == partitionNumber
                ? p with { SizeBytes = size, Volume = p.Volume is { } v ? v with { SizeBytes = size, FreeBytes = size - v.UsedBytes } : null }
                : p).ToList(),
        };
        return Task.FromResult(new ResizeBackendResult(true, null));
    }

    public Task<CreateVolumeBackendResult> CreateVolumeAsync(PhysicalDisk disk, long offsetBytes, long sizeBytes, string volumeLabel)
    {
        Calls++;
        if (Throw) throw new InvalidOperationException("simulated crash");
        if (FailCompletely) return Task.FromResult(new CreateVolumeBackendResult(false, false, null, "Access denied"));

        var number = disk.Partitions.Count == 0 ? 1 : disk.Partitions.Max(p => p.PartitionNumber) + 1;
        var volume = FailFormat ? null : new Volume("P", volumeLabel, "NTFS", sizeBytes, sizeBytes, ReportedHealth.Healthy, EncryptionState.NotEncrypted);
        var part = new Partition(disk.Number, number, offsetBytes, sizeBytes, PartitionKind.Basic, null, null, false, false, false, false, volume);
        var index = inventory.Disks.FindIndex(d => d.Number == disk.Number);
        inventory.Disks[index] = disk with { Partitions = disk.Partitions.Append(part).OrderBy(p => p.OffsetBytes).ToList() };

        return Task.FromResult(FailFormat
            ? new CreateVolumeBackendResult(true, false, null, "Format failed")
            : new CreateVolumeBackendResult(true, true, "P", null));
    }
}

public class OperationTests
{
    private const long MiB = ByteSize.MiB;
    private const string PracticeFolder = @"C:\Users\Test\AppData\Local\ShadowDisk\practice";

    private static PhysicalDisk PracticeDisk(int number = 7) =>
        new(number, "Msft Virtual Disk", null, 1024 * MiB, 512, 512, PartitionStyle.Gpt, BusType.FileBackedVirtual,
            false, false, false, false, ReportedHealth.Healthy,
            [new Partition(number, 1, MiB, 15 * MiB, PartitionKind.MicrosoftReserved, ShadowDisk.Core.Mapping.StorageMapper.GptMicrosoftReserved, null, false, false, false, true, null)],
            Location: PracticeFolder + @"\practice-1.vhd", UniqueId: "VHD-UNIQUE-1");

    private static (OperationRunner Runner, FakeInventory Inventory, FakeDiskOperations Ops, OperationLog Log) Setup(PhysicalDisk disk)
    {
        var inventory = new FakeInventory([disk]);
        var ops = new FakeDiskOperations(inventory);
        var log = new OperationLog(null);
        return (new OperationRunner(inventory, ops, new OperationPolicy(PracticeFolder), log), inventory, ops, log);
    }

    private static CreateVolumeOrder OrderFor(PhysicalDisk disk, long size)
    {
        var layout = DiskLayoutBuilder.Build(disk);
        var free = layout.Regions.First(r => r.Kind == RegionKind.Unallocated);
        var (offset, _) = PlanLimits.ForNewVolume(layout, free);
        return new CreateVolumeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk), offset, size, "Practice");
    }

    [Fact]
    public async Task Creates_a_volume_on_a_practice_disk_and_verifies_by_reading_back()
    {
        var disk = PracticeDisk();
        var (runner, inventory, ops, _) = Setup(disk);
        var outcome = await runner.CreateVolumeAsync(OrderFor(disk, 200 * MiB), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, outcome.Status);
        Assert.Equal(1, ops.Calls);
        Assert.Equal(2, inventory.Reads);            // once before, once after
        Assert.NotNull(outcome.StateAfter);
        Assert.Contains(outcome.Steps, s => s.Contains("practice disk"));
    }

    [Fact]
    public async Task Physical_disks_are_refused_before_anything_is_written()
    {
        var physical = PracticeDisk() with { Bus = BusType.Sata, Location = "PCI Slot 0" };
        var (runner, _, ops, _) = Setup(physical);
        var outcome = await runner.CreateVolumeAsync(OrderFor(physical, 200 * MiB), CancellationToken.None);

        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Equal(0, ops.Calls);
        Assert.Contains("practice disks", outcome.Message);
        Assert.False(outcome.DiskMayHaveChanged);
    }

    [Fact]
    public async Task A_virtual_disk_outside_the_practice_folder_is_refused()
    {
        var other = PracticeDisk() with { Location = @"D:\VMs\server.vhd" };
        var (runner, _, ops, _) = Setup(other);
        var outcome = await runner.CreateVolumeAsync(OrderFor(other, 200 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Equal(0, ops.Calls);
    }

    [Fact]
    public async Task A_changed_layout_since_planning_is_refused()
    {
        var disk = PracticeDisk();
        var (runner, inventory, ops, _) = Setup(disk);
        var order = OrderFor(disk, 200 * MiB);
        // Something else adds a partition between planning and applying.
        inventory.BeforeRead = () =>
        {
            inventory.Disks[0] = disk with { Partitions = disk.Partitions.Append(
                new Partition(7, 2, 500 * MiB, 100 * MiB, PartitionKind.Basic, null, null, false, false, false, false, null)).ToList() };
            inventory.BeforeRead = null;
        };
        var outcome = await runner.CreateVolumeAsync(order, CancellationToken.None);
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Contains("layout changed", outcome.Message);
        Assert.Equal(0, ops.Calls);
    }

    [Fact]
    public async Task A_different_disk_with_the_same_number_is_refused()
    {
        var disk = PracticeDisk();
        var (runner, inventory, ops, _) = Setup(disk);
        var order = OrderFor(disk, 200 * MiB);
        inventory.Disks[0] = disk with { UniqueId = "SOMETHING-ELSE" };
        var outcome = await runner.CreateVolumeAsync(order, CancellationToken.None);
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Equal(0, ops.Calls);
    }

    [Fact]
    public async Task A_size_that_no_longer_fits_is_refused()
    {
        var disk = PracticeDisk();
        var (runner, _, ops, _) = Setup(disk);
        var outcome = await runner.CreateVolumeAsync(OrderFor(disk, 5000 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Equal(0, ops.Calls);
    }

    [Fact]
    public async Task Cancelling_before_writing_changes_nothing()
    {
        var disk = PracticeDisk();
        var (runner, _, ops, _) = Setup(disk);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = await runner.CreateVolumeAsync(OrderFor(disk, 200 * MiB), cts.Token);
        Assert.Equal(OperationStatus.Cancelled, outcome.Status);
        Assert.Equal(0, ops.Calls);
    }

    [Fact]
    public async Task A_failure_is_reported_once_with_no_retry()
    {
        var disk = PracticeDisk();
        var (runner, _, ops, log) = Setup(disk);
        ops.FailCompletely = true;
        var outcome = await runner.CreateVolumeAsync(OrderFor(disk, 200 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, outcome.Status);
        Assert.Equal(1, ops.Calls);
        Assert.Contains("Access denied", outcome.Message);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_crash_in_the_backend_is_reported_not_thrown()
    {
        var disk = PracticeDisk();
        var (runner, _, ops, _) = Setup(disk);
        ops.Throw = true;
        var outcome = await runner.CreateVolumeAsync(OrderFor(disk, 200 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, outcome.Status);
        Assert.Equal(1, ops.Calls);
    }

    [Fact]
    public async Task Partition_created_but_format_failed_is_reported_as_partial()
    {
        var disk = PracticeDisk();
        var (runner, _, ops, _) = Setup(disk);
        ops.FailFormat = true;
        var outcome = await runner.CreateVolumeAsync(OrderFor(disk, 200 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.PartiallyApplied, outcome.Status);
        Assert.True(outcome.DiskMayHaveChanged);
        Assert.Contains("did not try to undo", outcome.Message);
        Assert.Equal(1, ops.Calls);
    }

    // ---------- Resize (shrink / extend) ----------

    private static PhysicalDisk PracticeDiskWithVolume(long volumeSize = 400 * MiB, long used = 50 * MiB)
    {
        var d = PracticeDisk();
        var vol = new Partition(d.Number, 2, 16 * MiB, volumeSize, PartitionKind.Basic, ShadowDisk.Core.Mapping.StorageMapper.GptBasicData, null,
            false, false, false, false, new Volume("P", "Practice", "NTFS", volumeSize, volumeSize - used, ReportedHealth.Healthy, EncryptionState.NotEncrypted));
        return d with { Partitions = d.Partitions.Append(vol).ToList() };
    }

    private static ResizeOrder ResizeFor(PhysicalDisk disk, long target)
    {
        var p = disk.Partitions.Single(x => x.PartitionNumber == 2);
        return new ResizeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk), 2, p.OffsetBytes, p.SizeBytes, target);
    }

    [Fact]
    public async Task Shrinks_a_practice_volume_and_verifies_the_new_size()
    {
        var disk = PracticeDiskWithVolume();
        var (runner, inventory, ops, _) = Setup(disk);
        var outcome = await runner.ResizeAsync(ResizeFor(disk, 200 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Succeeded, outcome.Status);
        Assert.Equal(1, ops.ResizeCalls);
        Assert.Equal(200 * MiB, inventory.Disks[0].Partitions.Single(p => p.PartitionNumber == 2).SizeBytes);
        Assert.Contains(outcome.Steps, s => s.Contains("size limits again"));
    }

    [Fact]
    public async Task Extends_a_practice_volume_into_the_free_space_after_it()
    {
        var disk = PracticeDiskWithVolume();
        var (runner, _, ops, _) = Setup(disk);
        var outcome = await runner.ResizeAsync(ResizeFor(disk, 800 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Succeeded, outcome.Status);
        Assert.Equal(1, ops.ResizeCalls);
    }

    [Fact]
    public async Task Shrink_below_the_fresh_limit_is_refused()
    {
        var disk = PracticeDiskWithVolume(used: 300 * MiB);
        var (runner, _, ops, _) = Setup(disk);
        var outcome = await runner.ResizeAsync(ResizeFor(disk, 200 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Equal(0, ops.ResizeCalls);
    }

    [Fact]
    public async Task Resize_on_a_physical_disk_is_refused()
    {
        var disk = PracticeDiskWithVolume() with { Bus = BusType.Nvme, Location = "PCI" };
        var (runner, _, ops, _) = Setup(disk);
        var outcome = await runner.ResizeAsync(ResizeFor(disk, 200 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Equal(0, ops.ResizeCalls);
    }

    [Fact]
    public async Task Resize_is_refused_when_the_partition_changed_size_since_planning()
    {
        var disk = PracticeDiskWithVolume();
        var (runner, inventory, ops, _) = Setup(disk);
        var order = ResizeFor(disk, 200 * MiB);
        inventory.Disks[0] = disk with { Partitions = disk.Partitions.Select(p => p.PartitionNumber == 2 ? p with { SizeBytes = 300 * MiB } : p).ToList() };
        var outcome = await runner.ResizeAsync(order, CancellationToken.None);
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Equal(0, ops.ResizeCalls);
    }

    [Fact]
    public async Task Resize_failure_is_reported_once_without_retry()
    {
        var disk = PracticeDiskWithVolume();
        var (runner, _, ops, _) = Setup(disk);
        ops.FailCompletely = true;
        var outcome = await runner.ResizeAsync(ResizeFor(disk, 200 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, outcome.Status);
        Assert.Equal(1, ops.ResizeCalls);
    }

    [Fact]
    public async Task Resize_to_a_different_size_than_asked_is_reported_as_partial()
    {
        var disk = PracticeDiskWithVolume();
        var (runner, _, ops, _) = Setup(disk);
        ops.ResizeTo = 250 * MiB;
        var outcome = await runner.ResizeAsync(ResizeFor(disk, 200 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.PartiallyApplied, outcome.Status);
        Assert.Contains("instead of", outcome.Message);
    }

    [Fact]
    public async Task Same_size_resize_does_nothing()
    {
        var disk = PracticeDiskWithVolume();
        var (runner, _, ops, _) = Setup(disk);
        var outcome = await runner.ResizeAsync(ResizeFor(disk, 400 * MiB), CancellationToken.None);
        Assert.Equal(OperationStatus.Blocked, outcome.Status);
        Assert.Equal(0, ops.ResizeCalls);
    }

    [Fact]
    public void Policy_recognises_only_practice_files()
    {
        var policy = new OperationPolicy(PracticeFolder + @"\");
        Assert.True(policy.IsPracticeDisk(PracticeDisk()));
        Assert.False(policy.IsPracticeDisk(PracticeDisk() with { Location = PracticeFolder + @"\notes.vhdx" }));
        Assert.False(policy.IsPracticeDisk(PracticeDisk() with { Location = null }));
        Assert.False(policy.IsPracticeDisk(PracticeDisk() with { Location = @"C:\Users\Test\AppData\Local\ShadowDisk\practice-evil\x.vhd" }));
        Assert.Contains(policy.WhyNotAllowed(PracticeDisk() with { IsSystem = true }), r => r.Contains("Windows runs"));
    }

    [Fact]
    public void Fingerprint_changes_when_a_partition_changes()
    {
        var disk = PracticeDisk();
        var changed = disk with { Partitions = [disk.Partitions[0] with { SizeBytes = 16 * MiB }] };
        Assert.NotEqual(LayoutFingerprint.Of(disk), LayoutFingerprint.Of(changed));
        Assert.Equal(LayoutFingerprint.Of(disk), LayoutFingerprint.Of(disk with { FriendlyName = "renamed" }));
    }
}
