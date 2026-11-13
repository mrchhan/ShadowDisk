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
