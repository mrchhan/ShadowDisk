using ShadowDisk.Core.Copying;
using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;
using ShadowDisk.Core.Recovery;

namespace ShadowDisk.Core.Wizard;

// Stage 5, parts 3 and 4: "Give space to the drive before".
//
//   Part 3:  | target | donor (files)                  | (free?) |
//         →  | target + given space        | donor' (same files, same letter) |
//
//   Part 4:  | C: | Recovery | donor (files)            | (free?) |
//         →  | C: + given space    | Recovery' | donor' (same files, same letter) |
//
//   (Part 4 also works when the space after Recovery is already free: then there is no donor to move.)
//
// Built only from operations that are each checked on their own: shrink, create volume, copy & verify,
// delete volume, Windows Recovery off / on (reagentc), delete / create the Recovery partition, extend,
// change drive letter. Each step re-reads the disk and is checked again. Nothing is retried silently.

public sealed record GiveSpacePlan(
    PhysicalDisk Disk,
    Partition Target,
    Partition? Donor,
    long DonorUsedBytes,
    long NeededForDonorFiles,   // size the new donor volume needs: its files plus headroom
    long AreaBytes,             // space that is rearranged: from the Recovery (or donor) start to the end of the free space after it
    long MinGive,
    long MaxGive,
    IReadOnlyList<string> Problems,
    Partition? Recovery = null,
    long NewRecoveryBytes = 0,
    IReadOnlyList<string>? Notes = null,
    bool RecoveryFirst = false)   // MBR disk already full (4 primary partitions): remove Recovery before creating the temporary volume
{
    public bool IsPossible => Problems.Count == 0;

    /// <summary>Where the rearranged space starts: the Recovery partition, or else the donor.</summary>
    public long SpaceStart => Recovery?.OffsetBytes ?? Donor?.OffsetBytes ?? Target.EndBytes;

    /// <summary>Approximate size of the recreated donor volume when <paramref name="give"/> is given.</summary>
    public long NewDonorBytes(long give) => Donor is null ? 0 : Math.Max(0, AreaBytes - give - NewRecoveryBytes);

    /// <summary>One sentence describing the result, for the wizard window.</summary>
    public string Describe(long give)
    {
        var grows = $"{Target.DisplayName} grows by {ByteSize.Format(give)} to about {ByteSize.Format(Target.SizeBytes + give)}.";
        var rec = Recovery is null ? "" : $" A new {ByteSize.Format(NewRecoveryBytes)} Recovery partition follows it.";
        var donor = Donor is null ? "" : $" The new {Donor.DisplayName} will be about {ByteSize.Format(NewDonorBytes(give))}.";
        return grows + rec + donor;
    }
}

public static class GiveSpacePlanner
{
    /// <summary>The new Recovery partition is at least this big, so later Windows updates to it fit (see KB5028997).</summary>
    public const long MinimumNewRecovery = ByteSize.GiB;

    /// <summary>
    /// Works out whether space can be given to <paramref name="target"/> from what follows it, and how much.
    /// <paramref name="donorShrinkLimits"/> are the donor's shrink limits (from Windows if possible).
    /// <paramref name="recovery"/> is Windows Recovery's status; needed only when a Recovery partition is in the way.
    /// </summary>
    public static GiveSpacePlan Plan(DiskLayout layout, Partition target, SizeLimits? donorShrinkLimits, RecoveryStatus? recovery = null)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var regions = layout.Regions.Where(r => r.Kind != RegionKind.Gap).ToList();
        var index = regions.FindIndex(r => ReferenceEquals(r.Partition, target));
        DiskRegion? At(int i) => index >= 0 && i < regions.Count ? regions[i] : null;

        var windows = OperationPolicy.IsExtendableWindowsVolume(target);
        if ((target.IsBoot || target.IsSystem || target.IsStartupPartition) && !windows)
            problems.Add($"{target.DisplayName} holds the files your PC needs to start. ShadowDisk never changes it.");
        else if (target.Kind is not (PartitionKind.Basic or PartitionKind.Logical) || target.Volume?.FileSystem is not ("NTFS" or "REFS"))
            problems.Add($"{target.DisplayName} must be an ordinary NTFS or ReFS data volume to be extended.");
        if (windows)
            notes.Add($"{target.DisplayName} is the Windows drive. Only extending it is done; it is never shrunk, moved or deleted. " +
                      "Make a full backup (for example a system image) before starting.");

        // What follows the target: an optional Recovery partition, then the donor volume or free space.
        var next = At(index + 1);
        Partition? rec = null;
        var after = next;
        if (next?.Partition is { Kind: PartitionKind.Recovery } r)
        {
            rec = r;
            after = At(index + 2);
            if (OperationRunner.WhyNotMovableRecovery(r) is { } whyNot)
                problems.Add($"A Recovery partition sits directly after {target.DisplayName}, but it can't be moved: {whyNot}");
            else if (recovery is null)
                problems.Add("ShadowDisk could not check Windows Recovery (reagentc). Use “Restart as administrator” and try again.");
            else if (!recovery.IsOn(layout.Disk.Number, r.PartitionNumber))
                problems.Add(recovery.Enabled
                    ? "Windows Recovery is not on the Recovery partition after " + target.DisplayName + ", so that partition may hold other " +
                      "tools (such as the PC maker's). ShadowDisk only moves the partition Windows Recovery itself uses."
                    : "Windows Recovery is turned off, so ShadowDisk can't confirm what the Recovery partition holds. Turn it on " +
                      "(run “reagentc /enable” as administrator) and try again.");
            else
                notes.Add("Windows Recovery is turned off for a few steps, then back on. Its image is kept in C:\\Windows\\System32\\Recovery meanwhile.");
        }

        Partition? donor = after?.Partition;
        if (next is null)
            problems.Add($"There is nothing after {target.DisplayName} on this disk to take space from.");
        else if (next.Kind == RegionKind.Unallocated)
            problems.Add($"There is already free space directly after {target.DisplayName}. Use “Plan a change” → Extend instead.");
        else if (rec is not null && after is null)
            problems.Add($"Only the Recovery partition follows {target.DisplayName}; there is no space to give.");
        else if (donor is not null)
        {
            if (donor.Kind == PartitionKind.Recovery)
                problems.Add("Two Recovery partitions follow each other here. ShadowDisk moves at most one.");
            else if (OperationRunner.WhyKindNotDeletable(donor) is { } kindReason)
                problems.Add($"{donor.DisplayName} sits directly after {(rec is null ? target.DisplayName : "the Recovery partition")}, but it can't be moved: {kindReason}");
            else if (donor.Volume?.DriveLetter is not { Length: > 0 })
                problems.Add($"{donor.DisplayName} has no drive letter, so its files can't be copied. Give it a letter first.");
            else if (donor.Volume.FileSystem is not ("NTFS" or "REFS"))
                problems.Add($"{donor.DisplayName} uses {donor.Volume.FileSystem ?? "an unknown file system"}; this wizard handles NTFS and ReFS volumes.");
        }

        // MBR disks hold at most four primary partitions. The temporary volume needs a free slot.
        var recoveryFirst = false;
        if (layout.Disk.Style == PartitionStyle.Mbr && donor is not null && problems.Count == 0)
        {
            if (layout.Disk.Partitions.Any(p => p.Kind is PartitionKind.Extended or PartitionKind.Logical))
                problems.Add("This MBR disk has an extended partition with logical drives. This wizard doesn't handle those yet.");
            else if (layout.Disk.Partitions.Count >= 4)
            {
                if (rec is null)
                    problems.Add("This MBR disk already has four partitions, the most it can hold, so there is no room for the temporary volume.");
                else
                {
                    recoveryFirst = true;
                    notes.Add("This MBR disk already has four partitions, the most it can hold. So the Recovery partition is removed first, " +
                              "to make room for the temporary volume, and Windows Recovery stays off while the files are copied. " +
                              "Its image stays safe in C:\\Windows\\System32\\Recovery the whole time.");
                }
            }
        }

        if (problems.Count > 0 || (donor is null && rec is null))
            return new GiveSpacePlan(layout.Disk, target, donor, 0, 0, 0, 0, 0, problems, rec, 0, notes);

        if (donor is not null)
            notes.Add($"{donor.DisplayName}'s files are copied one by one and keep the letter {donor.DisplayName}, but Windows file " +
                      $"permissions are not copied. Programs installed on {donor.DisplayName} may need repairing or reinstalling afterwards; " +
                      "documents, photos and other plain files are not affected.");

        var used = donor?.Volume!.UsedBytes ?? 0;
        var needed = donor is null ? 0 : PlanLimits.NeededForCopy(used);
        var newRec = rec is null ? 0 : Math.Min(OperationRunner.MaxMovableRecoveryBytes, Math.Max(MinimumNewRecovery, PlanLimits.AlignUp(rec.SizeBytes)));

        // End of the rearranged space: the donor's end, or the end of the free space after it (or after Recovery).
        var freeAfter = donor is null ? after : regions.Skip(regions.IndexOf(after!) + 1).FirstOrDefault();
        var lastEnd = donor?.EndBytes ?? rec!.EndBytes;
        long areaEnd = lastEnd;
        if (freeAfter is { Kind: RegionKind.Unallocated })
        {
            var (start, lim) = PlanLimits.ForNewVolume(layout, freeAfter);
            areaEnd = Math.Max(areaEnd, start + lim.MaxBytes);
        }
        // The very end of the disk can't hold a new partition (GPT backup table; MBR's last partial cylinder).
        areaEnd = Math.Min(areaEnd, layout.Disk.SizeBytes - PlanLimits.TailReserve(layout.Disk));
        var spaceStart = rec?.OffsetBytes ?? donor!.OffsetBytes;
        var area = areaEnd - spaceStart;
        var slack = rec is null ? 0 : 2 * PlanLimits.Alignment;   // room for rounding each new start up to a whole MiB

        long minGive, maxGive;
        long donorMin = 0;
        if (donor is not null)
        {
            // The donor's files stay in its first part while it is shrunk, so the copy must start after that.
            donorMin = donorShrinkLimits is { IsUsable: true } l ? PlanLimits.AlignUp(l.MinBytes) : donor.SizeBytes;
            minGive = Math.Max(PlanLimits.Alignment, PlanLimits.AlignUp(donorMin + (donor.OffsetBytes - spaceStart) - newRec));
            maxGive = PlanLimits.AlignDown(area - newRec - needed - slack);
        }
        else
        {
            minGive = PlanLimits.MinimumNewVolume;
            maxGive = PlanLimits.AlignDown(area - newRec - slack);
        }

        if (maxGive < minGive)
        {
            var whyMin = donor is null ? "" : donorMin >= donor.SizeBytes
                ? $" Windows can't shrink {donor.DisplayName} at the moment, so all of it would have to be given at once."
                : $" Windows can only shrink {donor.DisplayName} to {ByteSize.Format(donorMin)} (some of its files can't be moved by " +
                  $"Windows, for example the NTFS log), so at least that much must be given, but only {ByteSize.Format(Math.Max(0, maxGive))} is left.";
            problems.Add(donor is not null
                ? $"{donor.DisplayName} holds {ByteSize.Format(used)}. A safe copy of its files needs about {ByteSize.Format(needed)} of the " +
                  $"{ByteSize.Format(area)} available{(rec is null ? "" : $", and the new Recovery partition needs {ByteSize.Format(newRec)}")}." +
                  whyMin + $" Free up space on {donor.DisplayName}, move some files to another drive, or use a bigger volume."
                : $"After keeping a {ByteSize.Format(newRec)} Recovery partition, too little of the {ByteSize.Format(area)} is left to give.");
        }

        return new GiveSpacePlan(layout.Disk, target, donor, used, needed, area, minGive, maxGive, problems, rec, newRec, notes, recoveryFirst);
    }
}

public enum GiveSpaceStepKind
{
    ShrinkDonor, CreateTemporary, CopyAndVerify, DeleteDonor,
    DisableRecovery, DeleteRecovery,
    ExtendTarget,
    CreateRecovery, EnableRecovery,
    RenameTemporary,
    // Giving space to the NEXT volume (TakeSpaceWizard):
    ShrinkGiver, ShrinkReceiver, CreateNewVolume, CopyBack, DeleteTemporary,
}

public enum StepState { Pending, Done, Failed, Skipped }

public sealed class GiveSpaceStep(GiveSpaceStepKind kind, string title)
{
    public GiveSpaceStepKind Kind { get; } = kind;
    public string Title { get; } = title;
    public StepState State { get; internal set; }
    public string? Message { get; internal set; }
}

/// <summary>Runs the give-space steps one at a time. Each call to <see cref="RunNextAsync"/> runs one step.</summary>
public sealed class GiveSpaceWizard : ISpaceMoveWizard
{
    public delegate Task<CopyReport> CopyRunner(string source, string destination, bool resume, IProgress<CopyProgress>? progress, CancellationToken ct);

    private readonly IStorageInventory _inventory;
    private readonly OperationRunner _runner;
    private readonly IPartitionLimitsProvider _limits;
    private readonly Func<string, string> _rootOf;
    private readonly CopyRunner _copy;
    private readonly OperationLog _log;
    private readonly IRecoveryEnvironment? _recovery;
    private readonly int _disk;
    private readonly string? _diskId;
    private readonly long _targetOffset;
    private readonly bool _hasDonor;
    private readonly long _donorOffset;
    private readonly long _donorSize;
    private readonly string _donorLetter = "";
    private readonly string _donorLabel = "";
    private readonly bool _hasRecovery;
    private readonly long _oldRecoveryOffset;
    private readonly long _newRecoveryOffset;
    private readonly long _newRecoverySize;
    private readonly long _plannedTempOffset;
    private readonly long _give;
    private readonly long _needed;
    private long _tempOffset = -1;
    private string? _tempLetter;
    private int? _newRecoveryNumber;

    public GiveSpaceWizard(GiveSpacePlan plan, long giveBytes, IStorageInventory inventory, OperationRunner runner,
        IPartitionLimitsProvider limits, Func<string, string> rootOf, CopyRunner copy, OperationLog log,
        IRecoveryEnvironment? recovery = null)
    {
        if (!plan.IsPossible) throw new ArgumentException("The plan is not possible.", nameof(plan));
        if (plan.Recovery is not null && recovery is null) throw new ArgumentException("Moving Recovery needs the recovery environment.", nameof(recovery));
        _give = PlanLimits.AlignDown(Math.Clamp(giveBytes, plan.MinGive, plan.MaxGive));
        (_inventory, _runner, _limits, _rootOf, _copy, _log, _recovery) = (inventory, runner, limits, rootOf, copy, log, recovery);
        _disk = plan.Disk.Number;
        _diskId = plan.Disk.UniqueId;
        _targetOffset = plan.Target.OffsetBytes;
        _needed = plan.NeededForDonorFiles;
        TargetName = plan.Target.DisplayName;

        _hasRecovery = plan.Recovery is not null;
        if (_hasRecovery)
        {
            _oldRecoveryOffset = plan.Recovery!.OffsetBytes;
            _newRecoveryOffset = PlanLimits.AlignUp(plan.SpaceStart + _give);
            _newRecoverySize = plan.NewRecoveryBytes;
        }

        _hasDonor = plan.Donor is not null;
        if (_hasDonor)
        {
            _donorOffset = plan.Donor!.OffsetBytes;
            _donorSize = plan.Donor.SizeBytes;
            _donorLetter = plan.Donor.Volume!.DriveLetter!;
            _donorLabel = plan.Donor.Volume.Label ?? "";
            _plannedTempOffset = _hasRecovery
                ? PlanLimits.AlignUp(_newRecoveryOffset + _newRecoverySize)
                : PlanLimits.AlignUp(_donorOffset + _give);
        }

        var steps = new List<GiveSpaceStep>();
        void AddRecoveryOff()
        {
            if (!_hasRecovery) return;
            steps.Add(new(GiveSpaceStepKind.DisableRecovery, "Turn off Windows Recovery (its image is kept safe on the Windows drive)"));
            steps.Add(new(GiveSpaceStepKind.DeleteRecovery, "Delete the old Recovery partition"));
        }
        if (plan.RecoveryFirst) AddRecoveryOff();
        if (_hasDonor)
        {
            steps.Add(new(GiveSpaceStepKind.ShrinkDonor, $"Shrink {_donorLetter}: so there is room for a copy of its files"));
            steps.Add(new(GiveSpaceStepKind.CreateTemporary, "Create a temporary volume at the end of the space"));
            steps.Add(new(GiveSpaceStepKind.CopyAndVerify, $"Copy {_donorLetter}:'s files to it and verify every checksum"));
            steps.Add(new(GiveSpaceStepKind.DeleteDonor, $"Delete the old {_donorLetter}: (only after the copy is verified)"));
        }
        if (!plan.RecoveryFirst) AddRecoveryOff();
        steps.Add(new(GiveSpaceStepKind.ExtendTarget, $"Extend {TargetName} into the freed space"));
        if (_hasRecovery)
        {
            steps.Add(new(GiveSpaceStepKind.CreateRecovery, $"Create a new {ByteSize.Format(_newRecoverySize)} Recovery partition after {TargetName}"));
            steps.Add(new(GiveSpaceStepKind.EnableRecovery, "Turn Windows Recovery back on"));
        }
        if (_hasDonor)
            steps.Add(new(GiveSpaceStepKind.RenameTemporary, $"Give the new volume the letter {_donorLetter}:"));
        Steps = steps;

        if (_hasDonor && PlanLimits.AlignDown(_plannedTempOffset - _donorOffset) >= _donorSize)
        {
            var shrink = Step(GiveSpaceStepKind.ShrinkDonor)!;
            shrink.State = StepState.Skipped;
            shrink.Message = $"Not needed: the copy fits in the free space after {_donorLetter}:.";
        }
    }

    public string TargetName { get; }
    public long GiveBytes => _give;
    public IReadOnlyList<GiveSpaceStep> Steps { get; }
    public GiveSpaceStep? NextStep => Steps.FirstOrDefault(s => s.State is StepState.Pending or StepState.Failed);
    public bool IsFinished => NextStep is null;

    /// <summary>Planned position of the new Recovery partition (for tests and the log).</summary>
    public long NewRecoveryOffset => _newRecoveryOffset;

    private GiveSpaceStep? Step(GiveSpaceStepKind kind) => Steps.FirstOrDefault(s => s.Kind == kind);
    private void Say(GiveSpaceStepKind kind, string message) { if (Step(kind) is { } s) s.Message = message; }

    /// <summary>True between turning Windows Recovery off and turning it back on.</summary>
    private bool RecoveryIsOff =>
        Step(GiveSpaceStepKind.DisableRecovery)?.State == StepState.Done && Step(GiveSpaceStepKind.EnableRecovery)?.State != StepState.Done;

    /// <summary>
    /// Runs the next pending (or failed) step once. <paramref name="confirmDelete"/> is asked before the delete;
    /// returning false leaves that step pending and changes nothing.
    /// </summary>
    public async Task<GiveSpaceStep?> RunNextAsync(Func<string, bool> confirmDelete, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        var step = NextStep;
        if (step is null) return null;

        _log.Info($"Give space: step “{step.Title}”.");
        string? failure;
        try
        {
            failure = step.Kind switch
            {
                GiveSpaceStepKind.ShrinkDonor => await ShrinkAsync(ct),
                GiveSpaceStepKind.CreateTemporary => await CreateAsync(ct),
                GiveSpaceStepKind.CopyAndVerify => await CopyAsync(progress, ct),
                GiveSpaceStepKind.DeleteDonor => await DeleteAsync(confirmDelete, ct),
                GiveSpaceStepKind.DisableRecovery => await DisableRecoveryAsync(ct),
                GiveSpaceStepKind.DeleteRecovery => await DeleteRecoveryAsync(ct),
                GiveSpaceStepKind.ExtendTarget => await ExtendAsync(ct),
                GiveSpaceStepKind.CreateRecovery => await CreateRecoveryAsync(ct),
                GiveSpaceStepKind.EnableRecovery => await EnableRecoveryAsync(ct),
                _ => await RenameAsync(ct),
            };
        }
        catch (OperationCanceledException)
        {
            failure = "Cancelled. You can run this step again.";
        }
        catch (Exception ex)
        {
            failure = $"Unexpected problem: {ex.Message}";
        }

        if (failure == NotConfirmed)
        {
            step.Message = "Not confirmed. Nothing was changed.";
            return step;
        }
        step.State = failure is null ? StepState.Done : StepState.Failed;
        if (failure is not null)
        {
            step.Message = failure + " The wizard has stopped; nothing after this step was done.";
            if (RecoveryIsOff && _recovery is { IsSimulated: false })
                step.Message += " Windows Recovery is off at the moment; its image is safe in C:\\Windows\\System32\\Recovery. " +
                                "If you stop here, turn it back on by running “reagentc /enable” as administrator.";
            _log.Error($"Give space stopped at “{step.Title}”: {failure}");
        }
        return step;
    }

    private const string NotConfirmed = "\u0000not-confirmed";
    private const string ImageFolder = @"C:\Windows\System32\Recovery";

    private async Task<(PhysicalDisk Disk, DiskLayout Layout)> ReadAsync(CancellationToken ct)
    {
        var snapshot = await _inventory.GetSnapshotAsync(ct);
        var disk = snapshot.Disks.FirstOrDefault(d => d.Number == _disk && d.UniqueId == _diskId)
                   ?? throw new InvalidOperationException("The disk is no longer the same disk.");
        return (disk, DiskLayoutBuilder.Build(disk));
    }

    private static Partition? At(PhysicalDisk disk, long offset) => disk.Partitions.FirstOrDefault(p => p.OffsetBytes == offset);

    // ---- Moving the donor volume (part 3) ----

    private async Task<string?> ShrinkAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var donor = At(disk, _donorOffset);
        if (donor?.Volume?.DriveLetter != _donorLetter) return $"{_donorLetter}: is not where it was expected.";
        var target = PlanLimits.AlignDown(_plannedTempOffset - _donorOffset);
        if (donor.SizeBytes <= target)
        {
            Say(GiveSpaceStepKind.ShrinkDonor, "Already small enough.");
            return null;
        }
        var outcome = await _runner.ResizeAsync(new ResizeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk),
            donor.PartitionNumber, donor.OffsetBytes, donor.SizeBytes, target), ct);
        Say(GiveSpaceStepKind.ShrinkDonor, outcome.Message);
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    private async Task<string?> CreateAsync(CancellationToken ct)
    {
        var (disk, layout) = await ReadAsync(ct);
        var donor = At(disk, _donorOffset);
        var free = layout.Regions.FirstOrDefault(r => r.Kind == RegionKind.Unallocated
                                                      && r.OffsetBytes >= (donor?.EndBytes ?? _donorOffset)
                                                      && r.EndBytes > _plannedTempOffset);
        if (donor is null || free is null) return $"There is no free space after {_donorLetter}: for the temporary volume.";

        var (start, limits) = PlanLimits.ForNewVolume(layout, free);
        var offset = Math.Max(start, _plannedTempOffset);
        var size = PlanLimits.AlignDown(start + limits.MaxBytes - offset);
        if (size < _needed)
            return $"Only {ByteSize.Format(size)} is available for the copy, but {_donorLetter}:'s files need about {ByteSize.Format(_needed)}.";

        var outcome = await _runner.CreateVolumeAsync(new CreateVolumeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk),
            offset, size, _donorLabel), ct);
        Say(GiveSpaceStepKind.CreateTemporary, outcome.Message);
        if (outcome.Status != OperationStatus.Succeeded) return outcome.Message;

        var created = outcome.StateAfter?.Disks.FirstOrDefault(d => d.Number == _disk)?.Partitions.FirstOrDefault(p => p.OffsetBytes == offset);
        _tempOffset = offset;
        _tempLetter = created?.Volume?.DriveLetter;
        return _tempLetter is null ? "The temporary volume was created but has no drive letter, so files can't be copied to it." : null;
    }

    private async Task<string?> CopyAsync(IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        if (_tempLetter is null) return "The temporary volume is not known. Run the previous step first.";
        var source = _rootOf(_donorLetter);
        var destination = _rootOf(_tempLetter);
        var resume = VerifiedCopy.IsUnfinishedCopyOf(destination, source);
        var report = await _copy(source, destination, resume, progress, ct);
        Say(GiveSpaceStepKind.CopyAndVerify, report.Message);
        return report.IsVerified ? null : report.Message;
    }

    private async Task<string?> DeleteAsync(Func<string, bool> confirm, CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var donor = At(disk, _donorOffset);
        if (donor?.Volume?.DriveLetter != _donorLetter) return $"{_donorLetter}: is not where it was expected.";
        if (!confirm($"DELETE {_donorLetter}")) return NotConfirmed;

        var root = _rootOf(_donorLetter);
        var outcome = await _runner.DeleteAsync(
            new DeleteOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk), donor.PartitionNumber, donor.OffsetBytes, donor.SizeBytes),
            _ => DeleteSafety.WhyNotSafeToDelete(root, VerifiedCopyRegistry.Find(root), ct), ct);
        Say(GiveSpaceStepKind.DeleteDonor, outcome.Message);
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    // ---- Moving the Recovery partition (part 4) ----

    private string Where => _recovery!.IsSimulated ? " (simulated on this practice disk)" : "";

    private async Task<string?> DisableRecoveryAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var old = At(disk, _oldRecoveryOffset);
        if (old is not { Kind: PartitionKind.Recovery }) return "The Recovery partition is not where it was expected.";

        var status = await _recovery!.GetStatusAsync(ct);
        _log.Info("Windows Recovery before: " + status.Details);
        if (!status.Enabled && status.ImageOnWindowsDrive)
        {
            Say(GiveSpaceStepKind.DisableRecovery, $"Windows Recovery is already off and its image is in {ImageFolder}{Where}.");
            return null;
        }
        if (!status.IsOn(_disk, old.PartitionNumber))
            return "Windows Recovery is not on this Recovery partition any more, so ShadowDisk won't touch it.";

        var result = await _recovery.DisableAsync(ct);
        var now = await _recovery.GetStatusAsync(ct);
        _log.Info("Windows Recovery after turning off: " + now.Details);
        if (now.Enabled)
            return $"Windows Recovery could not be turned off ({result.Error ?? now.Details}). Nothing was changed.";
        if (!now.ImageOnWindowsDrive)
            return $"Windows Recovery was turned off, but its image was not found in {ImageFolder}. The Recovery partition was NOT touched. " +
                   "Turn Windows Recovery back on with “reagentc /enable” (as administrator) before doing anything else.";
        Say(GiveSpaceStepKind.DisableRecovery, $"Windows Recovery is off. Its image is safe in {ImageFolder}{Where}.");
        return null;
    }

    private async Task<string?> DeleteRecoveryAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var old = At(disk, _oldRecoveryOffset);
        if (old is null)
        {
            Say(GiveSpaceStepKind.DeleteRecovery, "Already deleted.");
            return null;
        }
        if (old.Kind != PartitionKind.Recovery) return "The partition at the Recovery position is not a Recovery partition. Nothing was changed.";

        var status = await _recovery!.GetStatusAsync(ct);
        var unsafeReason = status.Enabled
            ? "Windows Recovery is on again, so its partition is in use."
            : !status.ImageOnWindowsDrive ? $"The recovery image is not in {ImageFolder}, so the Recovery partition must stay." : null;

        var outcome = await _runner.DeleteRecoveryAsync(
            new DeleteOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk), old.PartitionNumber, old.OffsetBytes, old.SizeBytes),
            _ => unsafeReason, ct);
        Say(GiveSpaceStepKind.DeleteRecovery, outcome.Message);
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    private async Task<string?> CreateRecoveryAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        if (At(disk, _newRecoveryOffset) is { } existing)
        {
            if (existing.Kind != PartitionKind.Recovery || existing.SizeBytes != _newRecoverySize)
                return "Another partition is already where the new Recovery partition should go. Nothing was changed.";
            _newRecoveryNumber = existing.PartitionNumber;
            Say(GiveSpaceStepKind.CreateRecovery, "The new Recovery partition is already there.");
            return null;
        }
        var outcome = await _runner.CreateRecoveryPartitionAsync(new CreateVolumeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk),
            _newRecoveryOffset, _newRecoverySize, "Windows RE tools"), ct);
        Say(GiveSpaceStepKind.CreateRecovery, outcome.Message);
        if (outcome.Status != OperationStatus.Succeeded) return outcome.Message;
        _newRecoveryNumber = outcome.StateAfter?.Disks.FirstOrDefault(d => d.Number == _disk)?.Partitions
            .FirstOrDefault(p => p.OffsetBytes == _newRecoveryOffset)?.PartitionNumber;
        return null;
    }

    private async Task<string?> EnableRecoveryAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var created = At(disk, _newRecoveryOffset);
        if (created is not { Kind: PartitionKind.Recovery }) return "The new Recovery partition is not where it was expected.";
        _newRecoveryNumber = created.PartitionNumber;

        var result = await _recovery!.EnableAsync(ct);
        var now = await _recovery.GetStatusAsync(ct);
        _log.Info("Windows Recovery after turning on: " + now.Details);
        if (now.IsOn(_disk, created.PartitionNumber))
        {
            Say(GiveSpaceStepKind.EnableRecovery, $"Windows Recovery is on again, on the new Recovery partition{Where}.");
            return null;
        }
        if (now.Enabled)
        {
            Say(GiveSpaceStepKind.EnableRecovery, $"Windows Recovery is on again, but Windows chose to keep it on another partition{Where}. " +
                                                  "It works there too; the new Recovery partition is simply unused.");
            return null;
        }
        return $"Windows Recovery could not be turned back on ({result.Error ?? now.Details}). Its image is still in {ImageFolder}.";
    }

    // ---- Extend and finish ----

    private async Task<string?> ExtendAsync(CancellationToken ct)
    {
        var (disk, layout) = await ReadAsync(ct);
        var target = At(disk, _targetOffset);
        if (target is null) return $"{TargetName} is not where it was expected.";
        var limits = await _limits.GetLimitsAsync(layout, target, PlanKind.ExtendVolume, ct);

        long size;
        if (_hasRecovery)
        {
            // Leave exactly the room the new Recovery partition needs right after the target.
            size = PlanLimits.AlignDown(_newRecoveryOffset - _targetOffset);
            if (target.SizeBytes >= size)
            {
                Say(GiveSpaceStepKind.ExtendTarget, $"{TargetName} is already extended.");
                return null;
            }
            if (!limits.IsUsable || limits.MaxBytes < target.SizeBytes + PlanLimits.Alignment)
                return $"There is no free space directly after {TargetName} to extend into.";
            size = Math.Min(size, PlanLimits.AlignDown(limits.MaxBytes));
        }
        else
        {
            if (!limits.IsUsable) return $"There is no free space directly after {TargetName} to extend into.";
            size = limits.MaxBytes;
        }

        var outcome = await _runner.ResizeAsync(new ResizeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk),
            target.PartitionNumber, target.OffsetBytes, target.SizeBytes, size), ct);
        Say(GiveSpaceStepKind.ExtendTarget, outcome.Message);
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    private async Task<string?> RenameAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var temp = At(disk, _tempOffset);
        if (temp?.Volume is null) return "The new volume is not where it was expected.";
        if (string.Equals(temp.Volume.DriveLetter, _donorLetter, StringComparison.OrdinalIgnoreCase))
        {
            Say(GiveSpaceStepKind.RenameTemporary, $"It already has the letter {_donorLetter}:.");
            return null;
        }
        var outcome = await _runner.ChangeDriveLetterAsync(new DriveLetterOrder(disk.Number, disk.UniqueId, temp.PartitionNumber,
            temp.OffsetBytes, temp.Volume.DriveLetter, _donorLetter), ct);
        Say(GiveSpaceStepKind.RenameTemporary, outcome.Message);
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }
}
