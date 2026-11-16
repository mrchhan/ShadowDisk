using ShadowDisk.Core;
using ShadowDisk.Core.Copying;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;
using ShadowDisk.Core.Wizard;

namespace ShadowDisk.Core.Tests;

public sealed class GiveSpaceTests : IDisposable
{
    private const long MiB = ByteSize.MiB;
    private const string PracticeFolder = @"C:\Users\Test\AppData\Local\ShadowDisk\practice";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shadowdisk-give-" + Guid.NewGuid().ToString("N"));
    private string DonorDir => Path.Combine(_root, "G");
    private string TempDir => Path.Combine(_root, "P");

    public GiveSpaceTests()
    {
        Directory.CreateDirectory(Path.Combine(DonorDir, "Projects"));
        File.WriteAllText(Path.Combine(DonorDir, "Projects", "a.txt"), "alpha");
        File.WriteAllBytes(Path.Combine(DonorDir, "b.bin"), Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray());
        Directory.CreateDirectory(TempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static Volume Vol(string letter, long size, long used) =>
        new(letter, letter == "G" ? "Photos" : null, "NTFS", size, size - used, ReportedHealth.Healthy, EncryptionState.NotEncrypted);

    /// <summary>Practice disk: MSR | D: 300 MiB | G: 600 MiB (50 MiB used) | ~107 MiB free.</summary>
    private static PhysicalDisk Disk() =>
        new(7, "Msft Virtual Disk", null, 1024 * MiB, 512, 512, PartitionStyle.Gpt, BusType.FileBackedVirtual,
            false, false, false, false, ReportedHealth.Healthy,
            [
                new Partition(7, 1, MiB, 15 * MiB, PartitionKind.MicrosoftReserved, StorageMapper.GptMicrosoftReserved, null, false, false, false, true, null),
                new Partition(7, 2, 16 * MiB, 300 * MiB, PartitionKind.Basic, StorageMapper.GptBasicData, null, false, false, false, false, Vol("D", 300 * MiB, 10 * MiB)),
                new Partition(7, 3, 316 * MiB, 600 * MiB, PartitionKind.Basic, StorageMapper.GptBasicData, null, false, false, false, false, Vol("G", 600 * MiB, 50 * MiB)),
            ],
            Location: PracticeFolder + @"\practice-1.vhd", UniqueId: "VHD-1");

    private static GiveSpacePlan PlanFor(PhysicalDisk disk, string letter)
    {
        var layout = DiskLayoutBuilder.Build(disk);
        var target = disk.Partitions.Single(p => p.Volume?.DriveLetter == letter);
        var regions = layout.Regions.Where(r => r.Kind != RegionKind.Gap).ToList();
        var donor = regions.SkipWhile(r => !ReferenceEquals(r.Partition, target)).Skip(1).FirstOrDefault()?.Partition;
        var limits = donor?.Volume is not null ? PlanLimits.Estimate(layout, donor, PlanKind.ShrinkVolume) : null;
        return GiveSpacePlanner.Plan(layout, target, limits);
    }

    [Fact]
    public void Planner_works_out_how_much_can_be_given()
    {
        var plan = PlanFor(Disk(), "D");
        Assert.True(plan.IsPossible, string.Join(" ", plan.Problems));
        Assert.Equal("G", plan.Donor!.Volume!.DriveLetter);
        Assert.True(plan.NeededForDonorFiles >= 50 * MiB);
        Assert.True(plan.MaxGive > plan.MinGive);
        Assert.True(plan.MaxGive <= plan.AreaBytes - plan.NeededForDonorFiles);
    }

    [Fact]
    public async Task Runs_every_step_and_ends_with_a_bigger_target_and_the_same_letter()
    {
        var disk = Disk();
        var inventory = new FakeInventory([disk]);
        var ops = new FakeDiskOperations(inventory);
        var log = new OperationLog(null);
        var policy = new OperationPolicy(PracticeFolder);
        var runner = new OperationRunner(inventory, ops, policy, log);
        var plan = PlanFor(disk, "D");
        string RootOf(string l) => l == "G" ? DonorDir : TempDir;
        var wizard = new GiveSpaceWizard(plan, 400 * MiB, inventory, runner, new EstimatedLimitsProvider(), RootOf,
            (s, d, resume, p, ct) => new VerifiedCopy(log).CopyAndVerifyAsync(s, d, p, ct, resume), log);

        var confirmations = new List<string>();
        while (!wizard.IsFinished)
        {
            var step = await wizard.RunNextAsync(text => { confirmations.Add(text); return true; }, null, CancellationToken.None);
            Assert.NotEqual(StepState.Failed, step!.State);
        }

        Assert.Equal(["DELETE G"], confirmations);
        var final = inventory.Disks[0];
        var d = final.Partitions.Single(p => p.OffsetBytes == 16 * MiB);
        Assert.Equal(700 * MiB, d.SizeBytes);                                     // 300 + 400 given
        var newG = final.Partitions.Single(p => p.Volume?.DriveLetter == "G");
        Assert.True(newG.OffsetBytes >= 716 * MiB);                               // the new G: sits after the given space
        Assert.Equal("Photos", newG.Volume!.Label);      // and keeps the old G:'s label
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(TempDir, "Projects", "a.txt")));   // files copied
        Assert.Equal(1, ops.DeleteCalls);
    }

    [Fact]
    public async Task Declining_the_delete_changes_nothing_and_stops_there()
    {
        var disk = Disk();
        var inventory = new FakeInventory([disk]);
        var ops = new FakeDiskOperations(inventory);
        var log = new OperationLog(null);
        var runner = new OperationRunner(inventory, ops, new OperationPolicy(PracticeFolder), log);
        string RootOf(string l) => l == "G" ? DonorDir : TempDir;
        var wizard = new GiveSpaceWizard(PlanFor(disk, "D"), 400 * MiB, inventory, runner, new EstimatedLimitsProvider(), RootOf,
            (s, d, resume, p, ct) => new VerifiedCopy(log).CopyAndVerifyAsync(s, d, p, ct, resume), log);

        for (var i = 0; i < 4; i++) await wizard.RunNextAsync(_ => false, null, CancellationToken.None);
        Assert.Equal(GiveSpaceStepKind.DeleteDonor, wizard.NextStep!.Kind);
        Assert.Equal(StepState.Pending, wizard.NextStep.State);
        Assert.Equal(0, ops.DeleteCalls);
    }

    [Fact]
    public async Task A_failed_copy_stops_the_wizard_before_anything_is_deleted()
    {
        var disk = Disk();
        var inventory = new FakeInventory([disk]);
        var ops = new FakeDiskOperations(inventory);
        var log = new OperationLog(null);
        var runner = new OperationRunner(inventory, ops, new OperationPolicy(PracticeFolder), log);
        var wizard = new GiveSpaceWizard(PlanFor(disk, "D"), 400 * MiB, inventory, runner, new EstimatedLimitsProvider(),
            l => l == "G" ? DonorDir : TempDir,
            (_, _, _, _, _) => Task.FromResult(new CopyReport(CopyOutcome.Problems, "1 file had problems.", 1, 1, 0, ["x: checksum differs"], [])), log);

        for (var i = 0; i < 5; i++) await wizard.RunNextAsync(_ => true, null, CancellationToken.None);
        Assert.Equal(StepState.Failed, wizard.Steps.Single(s => s.Kind == GiveSpaceStepKind.CopyAndVerify).State);
        Assert.Equal(StepState.Pending, wizard.Steps.Single(s => s.Kind == GiveSpaceStepKind.DeleteDonor).State);
        Assert.Equal(0, ops.DeleteCalls);
    }

    [Fact]
    public void A_start_up_partition_is_never_a_target()
    {
        var disk = Disk();
        disk = disk with { Partitions = disk.Partitions.Select(p => p.PartitionNumber == 2 ? p with { IsSystem = true } : p).ToList() };
        var plan = PlanFor(disk, "D");
        Assert.False(plan.IsPossible);
        Assert.Contains(plan.Problems, p => p.Contains("needs to start"));
    }

    [Fact]
    public void The_windows_volume_can_be_a_target_with_a_backup_note()
    {
        var disk = Disk();
        disk = disk with { Partitions = disk.Partitions.Select(p => p.PartitionNumber == 2 ? p with { IsBoot = true } : p).ToList() };
        var plan = PlanFor(disk, "D");
        Assert.True(plan.IsPossible, string.Join(" ", plan.Problems));
        Assert.Contains(plan.Notes!, n => n.Contains("Windows drive"));
    }

    [Fact]
    public void A_recovery_partition_in_the_way_needs_windows_recovery_to_be_checked()
    {
        var disk = Disk();
        disk = disk with { Partitions = disk.Partitions.Select(p => p.PartitionNumber == 3 ? p with { Kind = PartitionKind.Recovery } : p).ToList() };
        var plan = PlanFor(disk, "D");
        Assert.False(plan.IsPossible);
        Assert.Contains(plan.Problems, p => p.Contains("could not check Windows Recovery"));
    }

    [Fact]
    public void Free_space_already_after_the_target_points_to_extend()
    {
        var disk = Disk();
        disk = disk with { Partitions = disk.Partitions.Where(p => p.PartitionNumber != 3).ToList() };
        var plan = PlanFor(disk, "D");
        Assert.Contains(plan.Problems, p => p.Contains("Extend instead"));
    }

    [Fact]
    public void A_donor_that_is_too_full_is_explained()
    {
        var disk = Disk();
        disk = disk with { Partitions = disk.Partitions.Select(p => p.PartitionNumber == 3 ? p with { Volume = Vol("G", 600 * MiB, 590 * MiB) } : p).ToList() };
        var plan = PlanFor(disk, "D");
        Assert.False(plan.IsPossible);
        Assert.Contains(plan.Problems, p => p.Contains("too little to give"));
    }
}
