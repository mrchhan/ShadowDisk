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

/// <summary>Drag-to-rebalance towards the next volume: D: gives space to G: after it.</summary>
public sealed class TakeSpaceTests : IDisposable
{
    private const long MiB = ByteSize.MiB;
    private const string PracticeFolder = @"C:\Users\Test\AppData\Local\ShadowDisk\practice";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shadowdisk-take-" + Guid.NewGuid().ToString("N"));
    private string Dir(string letter) => Path.Combine(_root, letter);

    public TakeSpaceTests()
    {
        foreach (var l in new[] { "G", "P", "Q", "R" }) Directory.CreateDirectory(Dir(l));
        Directory.CreateDirectory(Path.Combine(Dir("G"), "Photos", "2026"));
        File.WriteAllText(Path.Combine(Dir("G"), "Photos", "2026", "trip.txt"), "beach");
        File.WriteAllBytes(Path.Combine(Dir("G"), "big.bin"), Enumerable.Range(0, 500_000).Select(i => (byte)(i * 13)).ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static Volume Vol(string letter, long size, long used, string? label = null) =>
        new(letter, label, "NTFS", size, size - used, ReportedHealth.Healthy, EncryptionState.NotEncrypted);

    /// <summary>GPT practice disk: MSR | D: 400 MiB (100 used) | G: 600 MiB (50 used, "Photos") | ~23 MiB free.</summary>
    private static PhysicalDisk Disk(long gUsed = 50 * MiB, bool mbrFull = false) => mbrFull
        ? new(7, "Msft Virtual Disk", null, 2048 * MiB, 512, 512, PartitionStyle.Mbr, BusType.FileBackedVirtual,
            false, false, false, false, ReportedHealth.Healthy,
            [
                new Partition(7, 1, MiB, 100 * MiB, PartitionKind.Basic, null, 7, false, false, false, false, Vol("A", 100 * MiB, 10 * MiB)),
                new Partition(7, 2, 101 * MiB, 400 * MiB, PartitionKind.Basic, null, 7, false, false, false, false, Vol("D", 400 * MiB, 100 * MiB)),
                new Partition(7, 3, 501 * MiB, 600 * MiB, PartitionKind.Basic, null, 7, false, false, false, false, Vol("G", 600 * MiB, gUsed)),
                new Partition(7, 4, 1101 * MiB, 900 * MiB, PartitionKind.Basic, null, 7, false, false, false, false, Vol("H", 900 * MiB, 10 * MiB)),
            ],
            Location: PracticeFolder + @"\practice-1.vhd", UniqueId: "VHD-1")
        : new(7, "Msft Virtual Disk", null, 1040 * MiB, 512, 512, PartitionStyle.Gpt, BusType.FileBackedVirtual,
            false, false, false, false, ReportedHealth.Healthy,
            [
                new Partition(7, 1, MiB, 15 * MiB, PartitionKind.MicrosoftReserved, StorageMapper.GptMicrosoftReserved, null, false, false, false, true, null),
                new Partition(7, 2, 16 * MiB, 400 * MiB, PartitionKind.Basic, StorageMapper.GptBasicData, null, false, false, false, false, Vol("D", 400 * MiB, 100 * MiB)),
                new Partition(7, 3, 416 * MiB, 600 * MiB, PartitionKind.Basic, StorageMapper.GptBasicData, null, false, false, false, false, Vol("G", 600 * MiB, gUsed, "Photos")),
            ],
            Location: PracticeFolder + @"\practice-1.vhd", UniqueId: "VHD-1");

    private static TakeSpacePlan PlanFor(PhysicalDisk disk, string giverLetter)
    {
        var layout = DiskLayoutBuilder.Build(disk);
        var giver = disk.Partitions.Single(p => p.Volume?.DriveLetter == giverLetter);
        var receiver = disk.Partitions.Where(p => p.OffsetBytes > giver.OffsetBytes).OrderBy(p => p.OffsetBytes).FirstOrDefault();
        return TakeSpacePlanner.Plan(layout, giver,
            PlanLimits.Estimate(layout, giver, PlanKind.ShrinkVolume),
            receiver?.Volume is null ? null : PlanLimits.Estimate(layout, receiver, PlanKind.ShrinkVolume));
    }

    [Fact]
    public async Task D_gives_space_to_G_and_G_keeps_its_files_letter_and_label()
    {
        var disk = Disk();
        var inventory = new FakeInventory([disk]);
        var maxPartitions = 0;
        inventory.BeforeRead = () => maxPartitions = Math.Max(maxPartitions, inventory.Disks[0].Partitions.Count);
        var ops = new FakeDiskOperations(inventory);
        var log = new OperationLog(null);
        var runner = new OperationRunner(inventory, ops, new OperationPolicy(PracticeFolder), log);

        var plan = PlanFor(disk, "D");
        Assert.True(plan.IsPossible, string.Join(" ", plan.Problems));

        var confirmations = new List<string>();
        var wizard = new TakeSpaceWizard(plan, 200 * MiB, inventory, runner, new EstimatedLimitsProvider(), Dir,
            (s, d, resume, p, ct) => new VerifiedCopy(log).CopyAndVerifyAsync(s, d, p, ct, resume), log);
        Assert.Equal(10, wizard.Steps.Count);
        while (!wizard.IsFinished)
        {
            var step = await wizard.RunNextAsync(t => { confirmations.Add(t); return true; }, null, CancellationToken.None);
            Assert.True(step!.State != StepState.Failed, step.Title + ": " + step.Message);
        }

        var final = inventory.Disks[0];
        var d = final.Partitions.Single(p => p.Volume?.DriveLetter == "D");
        var g = final.Partitions.Single(p => p.Volume?.DriveLetter == "G");
        Assert.Equal(200 * MiB, d.SizeBytes);
        Assert.Equal(16 * MiB, d.OffsetBytes);                       // D: never moves
        Assert.Equal(216 * MiB, g.OffsetBytes);                      // G: now starts right after the smaller D:
        Assert.True(g.SizeBytes >= 800 * MiB, $"G: is {g.SizeBytes / MiB} MiB");
        Assert.Equal("Photos", g.Volume!.Label);
        Assert.Equal(3, final.Partitions.Count);                     // the temporary volume is gone
        Assert.Equal(["DELETE G", "DELETE P"], confirmations);        // the old G:, then the temporary copy
        Assert.Equal(2, ops.DeleteCalls);
        Assert.Equal("beach", File.ReadAllText(Path.Combine(Dir("Q"), "Photos", "2026", "trip.txt")));   // the new volume (Q:) got the files back
        Assert.True(maxPartitions <= 4);
    }

    [Fact]
    public async Task Declining_a_delete_changes_nothing_and_stays_on_that_step()
    {
        var disk = Disk();
        var inventory = new FakeInventory([disk]);
        var ops = new FakeDiskOperations(inventory);
        var log = new OperationLog(null);
        var runner = new OperationRunner(inventory, ops, new OperationPolicy(PracticeFolder), log);
        var plan = PlanFor(disk, "D");
        var wizard = new TakeSpaceWizard(plan, 100 * MiB, inventory, runner, new EstimatedLimitsProvider(), Dir,
            (s, dd, resume, p, ct) => new VerifiedCopy(log).CopyAndVerifyAsync(s, dd, p, ct, resume), log);
        for (var i = 0; i < 4; i++) await wizard.RunNextAsync(_ => true, null, CancellationToken.None);   // shrink, shrink, temp, copy
        var step = await wizard.RunNextAsync(_ => false, null, CancellationToken.None);
        Assert.Equal(GiveSpaceStepKind.DeleteDonor, step!.Kind);
        Assert.Equal(StepState.Pending, step.State);
        Assert.Equal(0, ops.DeleteCalls);
        Assert.Contains(inventory.Disks[0].Partitions, p => p.Volume?.DriveLetter == "G");
    }

    [Fact]
    public void A_receiver_that_is_too_full_is_explained()
    {
        var plan = PlanFor(Disk(gUsed: 450 * MiB), "D");
        Assert.False(plan.IsPossible);
        Assert.Contains(plan.Problems, p => p.Contains("less than about half full"));
    }

    [Fact]
    public void The_windows_volume_never_gives_space()
    {
        var disk = Disk();
        disk = disk with { Partitions = disk.Partitions.Select(p => p.Volume?.DriveLetter == "D" ? p with { IsBoot = true } : p).ToList() };
        var plan = PlanFor(disk, "D");
        Assert.Contains(plan.Problems, p => p.Contains("only ever grow"));
    }

    [Fact]
    public void A_full_mbr_disk_has_no_room_for_the_temporary_volume()
    {
        var plan = PlanFor(Disk(mbrFull: true), "D");
        Assert.Contains(plan.Problems, p => p.Contains("four partitions"));
    }

    [Fact]
    public void Nothing_after_the_giver_is_explained()
    {
        var plan = PlanFor(Disk(), "G");
        Assert.Contains(plan.Problems, p => p.Contains("no volume directly after"));
    }
}
