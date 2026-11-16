using ShadowDisk.Core;
using ShadowDisk.Core.Copying;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;
using ShadowDisk.Core.Recovery;
using ShadowDisk.Core.Wizard;

namespace ShadowDisk.Core.Tests;

/// <summary>Windows Recovery whose answers a test can control, to check the wizard stops safely.</summary>
internal sealed class FakeRecovery(RecoveryStatus start) : IRecoveryEnvironment
{
    public RecoveryStatus Status { get; set; } = start;
    public RecoveryStatus? AfterDisable { get; set; }
    public int DisableCalls { get; private set; }
    public bool IsSimulated => false;
    public Task<RecoveryStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status);
    public Task<BackendResult> DisableAsync(CancellationToken ct)
    {
        DisableCalls++;
        Status = AfterDisable ?? Status with { Enabled = false, DiskNumber = null, PartitionNumber = null, ImageOnWindowsDrive = true };
        return Task.FromResult(new BackendResult(true, null));
    }
    public Task<BackendResult> EnableAsync(CancellationToken ct) => Task.FromResult(new BackendResult(false, "not used"));
}

public sealed class RecoveryMoveTests : IDisposable
{
    private const long MiB = ByteSize.MiB;
    private const string PracticeFolder = @"C:\Users\Test\AppData\Local\ShadowDisk\practice";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shadowdisk-rec-" + Guid.NewGuid().ToString("N"));
    private string DonorDir => Path.Combine(_root, "E");
    private string TempDir => Path.Combine(_root, "P");

    public RecoveryMoveTests()
    {
        Directory.CreateDirectory(Path.Combine(DonorDir, "Work"));
        File.WriteAllText(Path.Combine(DonorDir, "Work", "notes.txt"), "keep me");
        File.WriteAllBytes(Path.Combine(DonorDir, "data.bin"), Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray());
        Directory.CreateDirectory(TempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static Volume Vol(string? letter, long size, long used, string? label = null) =>
        new(letter, label, "NTFS", size, size - used, ReportedHealth.Healthy, EncryptionState.NotEncrypted);

    /// <summary>Like the test PC's Disk 0, smaller: MBR | System Reserved | C: (Windows) | Recovery 682 MiB | E: 3 GiB. Four partitions.</summary>
    private static PhysicalDisk WindowsMbrDisk() =>
        new(0, "Hitachi", "SN", 5853 * MiB, 512, 4096, PartitionStyle.Mbr, BusType.Raid,
            true, true, false, false, ReportedHealth.Healthy,
            [
                new Partition(0, 1, MiB, 50 * MiB, PartitionKind.Basic, null, 7, false, true, true, false, Vol(null, 50 * MiB, 30 * MiB, "System Reserved")),
                new Partition(0, 2, 51 * MiB, 2048 * MiB, PartitionKind.Basic, null, 7, true, false, false, false, Vol("C", 2048 * MiB, 1500 * MiB)),
                new Partition(0, 3, 2099 * MiB, 682 * MiB, PartitionKind.Recovery, null, 0x27, false, false, false, true, Vol(null, 682 * MiB, 500 * MiB)),
                new Partition(0, 4, 2781 * MiB, 3072 * MiB, PartitionKind.Basic, null, 7, false, false, false, false, Vol("E", 3072 * MiB, 100 * MiB, "Data")),
            ],
            Location: "PCI Slot 0", UniqueId: "REAL-DISK-0");

    /// <summary>GPT practice disk: MSR | D: | Recovery 600 MiB | ~1 GiB free.</summary>
    private static PhysicalDisk PracticeWithFreeAfterRecovery() =>
        new(7, "Msft Virtual Disk", null, 2048 * MiB, 512, 512, PartitionStyle.Gpt, BusType.FileBackedVirtual,
            false, false, false, false, ReportedHealth.Healthy,
            [
                new Partition(7, 1, MiB, 15 * MiB, PartitionKind.MicrosoftReserved, StorageMapper.GptMicrosoftReserved, null, false, false, false, true, null),
                new Partition(7, 2, 16 * MiB, 400 * MiB, PartitionKind.Basic, StorageMapper.GptBasicData, null, false, false, false, false, Vol("D", 400 * MiB, 50 * MiB)),
                new Partition(7, 3, 416 * MiB, 600 * MiB, PartitionKind.Recovery, StorageMapper.GptRecovery, null, false, false, false, true, Vol(null, 600 * MiB, 400 * MiB)),
            ],
            Location: PracticeFolder + @"\practice-2.vhd", UniqueId: "VHD-2");

    private static GiveSpacePlan PlanFor(PhysicalDisk disk, string letter, RecoveryStatus? status)
    {
        var layout = DiskLayoutBuilder.Build(disk);
        var target = disk.Partitions.Single(p => p.Volume?.DriveLetter == letter);
        var donor = disk.Partitions.FirstOrDefault(p => p.Kind == PartitionKind.Basic && p.OffsetBytes > target.OffsetBytes);
        var limits = donor?.Volume is not null ? PlanLimits.Estimate(layout, donor, PlanKind.ShrinkVolume) : null;
        return GiveSpacePlanner.Plan(layout, target, limits, status);
    }

    private static RecoveryStatus OnPartition(int disk, int partition) => new(true, disk, partition, false, "on");

    [Fact]
    public async Task Windows_drive_gets_space_on_a_full_mbr_disk_and_recovery_is_recreated()
    {
        var disk = WindowsMbrDisk();
        var inventory = new FakeInventory([disk]);
        var maxPartitions = 0;
        inventory.BeforeRead = () => maxPartitions = Math.Max(maxPartitions, inventory.Disks[0].Partitions.Count);
        var ops = new FakeDiskOperations(inventory);
        var log = new OperationLog(null);
        var policy = new OperationPolicy(PracticeFolder) { AllowRealDisks = true };
        var runner = new OperationRunner(inventory, ops, policy, log, null, () => true);
        var recovery = new SimulatedRecoveryEnvironment(inventory, 0, "REAL-DISK-0");

        var plan = PlanFor(disk, "C", await recovery.GetStatusAsync(CancellationToken.None));
        Assert.True(plan.IsPossible, string.Join(" ", plan.Problems));
        Assert.True(plan.RecoveryFirst);                        // four partitions already: Recovery goes first
        Assert.Equal(ByteSize.GiB, plan.NewRecoveryBytes);      // 682 MiB grows to 1 GiB

        string RootOf(string l) => l == "E" ? DonorDir : TempDir;
        var wizard = new GiveSpaceWizard(plan, plan.MaxGive, inventory, runner, new EstimatedLimitsProvider(), RootOf,
            (s, d, resume, p, ct) => new VerifiedCopy(log).CopyAndVerifyAsync(s, d, p, ct, resume), log, recovery);
        Assert.Equal(GiveSpaceStepKind.DisableRecovery, wizard.Steps[0].Kind);

        var confirmations = new List<string>();
        while (!wizard.IsFinished)
        {
            var step = await wizard.RunNextAsync(text => { confirmations.Add(text); return true; }, null, CancellationToken.None);
            Assert.True(step!.State != StepState.Failed, step.Title + ": " + step.Message);
        }

        var final = inventory.Disks[0];
        var c = final.Partitions.Single(p => p.Volume?.DriveLetter == "C");
        var rec = final.Partitions.Single(p => p.Kind == PartitionKind.Recovery);
        var e = final.Partitions.Single(p => p.Volume?.DriveLetter == "E");
        Assert.Equal(51 * MiB, c.OffsetBytes);                   // C: never moves
        Assert.True(c.SizeBytes > 2048 * MiB + 1024 * MiB, $"C: is {c.SizeBytes / MiB} MiB");
        Assert.True(rec.OffsetBytes >= c.EndBytes && rec.EndBytes <= e.OffsetBytes);   // C: | Recovery | E:
        Assert.Equal(ByteSize.GiB, rec.SizeBytes);
        Assert.Equal("Data", e.Volume!.Label);
        Assert.True(e.EndBytes <= final.SizeBytes - PlanLimits.MbrTailReserve);   // never into MBR's unusable last cylinder
        Assert.True(maxPartitions <= 4, $"the MBR disk had {maxPartitions} partitions at one point");
        Assert.Equal(["DELETE E"], confirmations);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(TempDir, "Work", "notes.txt")));
        Assert.True((await recovery.GetStatusAsync(CancellationToken.None)).IsOn(0, rec.PartitionNumber));
        Assert.Equal(2, ops.DeleteCalls);                        // old E: and old Recovery
        Assert.Equal(1, ops.RecoveryCreateCalls);
    }

    [Fact]
    public async Task Free_space_after_recovery_needs_no_copy()
    {
        var disk = PracticeWithFreeAfterRecovery();
        var inventory = new FakeInventory([disk]);
        var ops = new FakeDiskOperations(inventory);
        var log = new OperationLog(null);
        var runner = new OperationRunner(inventory, ops, new OperationPolicy(PracticeFolder), log);
        var recovery = new SimulatedRecoveryEnvironment(inventory, 7, "VHD-2");

        var plan = PlanFor(disk, "D", await recovery.GetStatusAsync(CancellationToken.None));
        Assert.True(plan.IsPossible, string.Join(" ", plan.Problems));
        Assert.Null(plan.Donor);

        var wizard = new GiveSpaceWizard(plan, plan.MaxGive, inventory, runner, new EstimatedLimitsProvider(), _ => TempDir,
            (_, _, _, _, _) => throw new InvalidOperationException("no copy expected"), log, recovery);
        Assert.Equal(
            [GiveSpaceStepKind.DisableRecovery, GiveSpaceStepKind.DeleteRecovery, GiveSpaceStepKind.ExtendTarget,
             GiveSpaceStepKind.CreateRecovery, GiveSpaceStepKind.EnableRecovery],
            wizard.Steps.Select(s => s.Kind));
        while (!wizard.IsFinished)
        {
            var step = await wizard.RunNextAsync(_ => true, null, CancellationToken.None);
            Assert.True(step!.State != StepState.Failed, step.Title + ": " + step.Message);
        }
        var final = inventory.Disks[0];
        var d = final.Partitions.Single(p => p.Volume?.DriveLetter == "D");
        Assert.True(d.SizeBytes > 400 * MiB);
        Assert.Contains(final.Partitions, p => p.Kind == PartitionKind.Recovery && p.OffsetBytes >= d.EndBytes);
    }

    [Fact]
    public async Task Recovery_partition_is_not_touched_if_its_image_does_not_reach_the_windows_drive()
    {
        var disk = WindowsMbrDisk();
        var inventory = new FakeInventory([disk]);
        var ops = new FakeDiskOperations(inventory);
        var log = new OperationLog(null);
        var runner = new OperationRunner(inventory, ops, new OperationPolicy(PracticeFolder) { AllowRealDisks = true }, log, null, () => true);
        var recovery = new FakeRecovery(OnPartition(0, 3)) { AfterDisable = new RecoveryStatus(false, null, null, false, "off, image missing") };

        var plan = PlanFor(disk, "C", recovery.Status);
        var wizard = new GiveSpaceWizard(plan, plan.MaxGive, inventory, runner, new EstimatedLimitsProvider(), _ => TempDir,
            (_, _, _, _, _) => throw new InvalidOperationException("no copy expected"), log, recovery);

        var first = await wizard.RunNextAsync(_ => true, null, CancellationToken.None);
        Assert.Equal(StepState.Failed, first!.State);
        Assert.Contains("NOT touched", first.Message);
        var again = await wizard.RunNextAsync(_ => true, null, CancellationToken.None);   // retrying runs the same step, never the delete
        Assert.Equal(GiveSpaceStepKind.DisableRecovery, again!.Kind);
        Assert.Equal(0, ops.DeleteCalls);
    }

    [Fact]
    public void Recovery_that_windows_does_not_use_is_never_moved()
    {
        var plan = PlanFor(WindowsMbrDisk(), "C", OnPartition(0, 9));
        Assert.False(plan.IsPossible);
        Assert.Contains(plan.Problems, p => p.Contains("only moves the partition Windows Recovery itself uses"));

        var off = PlanFor(WindowsMbrDisk(), "C", new RecoveryStatus(false, null, null, true, "off"));
        Assert.Contains(off.Problems, p => p.Contains("reagentc /enable"));
    }

    [Fact]
    public void Big_recovery_partitions_are_never_moved()
    {
        var disk = WindowsMbrDisk();
        disk = disk with { Partitions = disk.Partitions.Select(p => p.PartitionNumber == 3 ? p with { SizeBytes = 3 * ByteSize.GiB } : p).ToList() };
        var plan = PlanFor(disk, "C", OnPartition(0, 3));
        Assert.Contains(plan.Problems, p => p.Contains("factory"));
    }

    [Fact]
    public void Full_mbr_disk_without_recovery_has_no_room_for_the_temporary_volume()
    {
        var disk = WindowsMbrDisk();
        disk = disk with { Partitions = disk.Partitions.Select(p => p.PartitionNumber == 3 ? p with { Kind = PartitionKind.Basic, MbrType = 7, IsHidden = false, Volume = Vol("R", 682 * MiB, 10 * MiB) } : p).ToList() };
        var layout = DiskLayoutBuilder.Build(disk);
        var r = disk.Partitions.Single(p => p.PartitionNumber == 3);
        var plan = GiveSpacePlanner.Plan(layout, r, PlanLimits.Estimate(layout, disk.Partitions[3], PlanKind.ShrinkVolume));
        Assert.Contains(plan.Problems, p => p.Contains("four partitions"));
    }

    [Fact]
    public void Windows_drive_may_only_be_extended()
    {
        var disk = WindowsMbrDisk();
        var policy = new OperationPolicy(PracticeFolder) { AllowRealDisks = true };
        var c = disk.Partitions[1];
        Assert.Empty(policy.WhyNotAllowed(disk, c, true, extend: true));
        Assert.Contains(policy.WhyNotAllowed(disk, c, true), r => r.Contains("holds Windows"));                       // shrink, delete…
        Assert.Contains(policy.WhyNotAllowed(disk, disk.Partitions[0], true, extend: true), r => r.Contains("holds Windows")); // System Reserved
    }
}
