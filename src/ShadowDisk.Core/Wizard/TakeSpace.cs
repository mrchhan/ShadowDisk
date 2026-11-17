using ShadowDisk.Core.Copying;
using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.Core.Wizard;

// Drag-to-rebalance, the other direction: give space from a volume to the volume AFTER it.
//
//   | giver (L)          | receiver (R: files)          | (free?) |
// → | giver'  | receiver' (same files, same letter, bigger)        |
//
// Windows can't grow a volume towards the start of the disk, and moving a partition block by block is
// unsafe if power fails half-way. So R's files are moved twice, each time as a verified copy:
//   1. shrink L                        → free space before R
//   2. shrink R (keep its files)       → room at R's end
//   3. temporary volume at R's end     4. copy & verify R → temporary
//   5. delete old R (confirmed)        6. new volume from L's new end up to the temporary volume
//   7. copy & verify temporary → new   8. delete temporary (confirmed)
//   9. extend new volume over the rest 10. give it R's letter
// At every moment at least one verified copy of R's files exists.

/// <summary>A wizard the "move space" window can run, one step at a time.</summary>
public interface ISpaceMoveWizard
{
    string TargetName { get; }
    long GiveBytes { get; }
    IReadOnlyList<GiveSpaceStep> Steps { get; }
    GiveSpaceStep? NextStep { get; }
    bool IsFinished { get; }
    Task<GiveSpaceStep?> RunNextAsync(Func<string, bool> confirmDelete, IProgress<CopyProgress>? progress, CancellationToken ct);
}

public sealed record TakeSpacePlan(
    PhysicalDisk Disk,
    Partition Giver,
    Partition Receiver,
    long ReceiverUsedBytes,
    long NeededForFiles,
    long MinGive,
    long MaxGive,
    long TempOffset,
    long AreaEnd,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes)
{
    public bool IsPossible => Problems.Count == 0;

    /// <summary>One sentence describing the result, for the wizard window.</summary>
    public string Describe(long give) =>
        $"{Receiver.DisplayName} grows by {ByteSize.Format(give)} to about {ByteSize.Format(AreaEnd - Receiver.OffsetBytes + give)}. " +
        $"{Giver.DisplayName} shrinks to about {ByteSize.Format(Giver.SizeBytes - give)}.";
}

public static class TakeSpacePlanner
{
    /// <summary>
    /// Can <paramref name="giver"/> give space to the volume directly after it, and how much?
    /// The shrink limits should come from Windows where possible.
    /// </summary>
    public static TakeSpacePlan Plan(DiskLayout layout, Partition giver, SizeLimits? giverShrinkLimits, SizeLimits? receiverShrinkLimits)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var regions = layout.Regions.Where(r => r.Kind != RegionKind.Gap).ToList();
        var index = regions.FindIndex(r => ReferenceEquals(r.Partition, giver));
        var receiver = index >= 0 && index + 1 < regions.Count ? regions[index + 1].Partition : null;

        TakeSpacePlan Refuse() => new(layout.Disk, giver, receiver ?? giver, 0, 0, 0, 0, 0, 0, problems, notes);

        if (giver.IsBoot || giver.IsSystem || giver.IsStartupPartition)
            problems.Add($"{giver.DisplayName} is a Windows or start-up partition. It can only ever grow, never shrink.");
        else if (giver.Kind is not (PartitionKind.Basic or PartitionKind.Logical) || giver.Volume?.FileSystem is not ("NTFS" or "REFS"))
            problems.Add($"{giver.DisplayName} must be an ordinary NTFS or ReFS data volume to give space.");

        if (receiver is null)
            problems.Add($"There is no volume directly after {giver.DisplayName} to give space to.");
        else if (OperationRunner.WhyKindNotDeletable(receiver) is { } kindReason)
            problems.Add($"{receiver.DisplayName} sits directly after {giver.DisplayName}, but it can't be moved: {kindReason}");
        else if (receiver.Volume?.DriveLetter is not { Length: > 0 })
            problems.Add($"{receiver.DisplayName} has no drive letter, so its files can't be copied. Give it a letter first.");
        else if (receiver.Volume.FileSystem is not ("NTFS" or "REFS"))
            problems.Add($"{receiver.DisplayName} uses {receiver.Volume.FileSystem ?? "an unknown file system"}; this wizard handles NTFS and ReFS volumes.");

        if (problems.Count == 0 && layout.Disk.Style == PartitionStyle.Mbr)
        {
            if (layout.Disk.Partitions.Any(p => p.Kind is PartitionKind.Extended or PartitionKind.Logical))
                problems.Add("This MBR disk has an extended partition with logical drives. This wizard doesn't handle those yet.");
            else if (layout.Disk.Partitions.Count >= 4)
                problems.Add("This MBR disk already has four partitions, the most it can hold, so there is no room for the temporary volume.");
        }
        if (problems.Count > 0) return Refuse();
        var r = receiver!;

        // How much the giver can give: Windows' shrink limit.
        if (giverShrinkLimits is not { IsUsable: true } gl)
        {
            problems.Add($"Windows can't shrink {giver.DisplayName} right now, so it has no space to give.");
            return Refuse();
        }
        var minGive = PlanLimits.MinimumNewVolume;
        var maxGive = PlanLimits.AlignDown(giver.SizeBytes - PlanLimits.AlignUp(gl.MinBytes));

        // The receiver's files go to a temporary volume at the end of its space, so that end must hold them.
        var used = r.Volume!.UsedBytes;
        var needed = PlanLimits.NeededForCopy(used);
        var receiverKeep = receiverShrinkLimits is { IsUsable: true } rl ? PlanLimits.AlignUp(rl.MinBytes) : r.SizeBytes;

        var areaEnd = r.EndBytes;
        var after = regions.ElementAtOrDefault(index + 2);
        if (after is { Kind: RegionKind.Unallocated })
        {
            var (start, lim) = PlanLimits.ForNewVolume(layout, after);
            areaEnd = Math.Max(areaEnd, start + lim.MaxBytes);
        }
        areaEnd = Math.Min(areaEnd, layout.Disk.SizeBytes - PlanLimits.TailReserve(layout.Disk));
        var tempOffset = PlanLimits.AlignDown(areaEnd - needed);

        if (tempOffset < PlanLimits.AlignUp(r.OffsetBytes + receiverKeep) + PlanLimits.Alignment)
            problems.Add($"{r.DisplayName} holds {ByteSize.Format(used)}. To move it safely, ShadowDisk keeps a verified copy at the end of its " +
                         $"own space, which needs about {ByteSize.Format(needed)} free there. Free up space on {r.DisplayName} " +
                         "(it needs to be less than about half full) or move some files to another drive first.");
        else if (maxGive < minGive)
            problems.Add($"Windows can't shrink {giver.DisplayName} by enough to give space (its files leave too little free).");

        notes.Add($"{r.DisplayName}'s files are copied twice (out, then back), each copy checked file by file, and it keeps the letter " +
                  $"{r.DisplayName}. Windows file permissions are not copied, so programs installed on {r.DisplayName} may need repairing.");

        return new TakeSpacePlan(layout.Disk, giver, r, used, needed, minGive, Math.Max(minGive, maxGive), tempOffset, areaEnd, problems, notes);
    }
}

/// <summary>Runs the take-space steps one at a time (see the file header).</summary>
public sealed class TakeSpaceWizard : ISpaceMoveWizard
{
    private readonly IStorageInventory _inventory;
    private readonly OperationRunner _runner;
    private readonly IPartitionLimitsProvider _limits;
    private readonly Func<string, string> _rootOf;
    private readonly GiveSpaceWizard.CopyRunner _copy;
    private readonly OperationLog _log;
    private readonly int _disk;
    private readonly string? _diskId;
    private readonly long _giverOffset;
    private readonly long _giverNewSize;
    private readonly long _receiverOffset;
    private readonly string _letter;
    private readonly string _label;
    private readonly long _tempOffset;
    private readonly long _needed;
    private long _newOffset = -1;
    private long _tempAt = -1;
    private string? _tempLetter;
    private string? _newLetter;

    public TakeSpaceWizard(TakeSpacePlan plan, long giveBytes, IStorageInventory inventory, OperationRunner runner,
        IPartitionLimitsProvider limits, Func<string, string> rootOf, GiveSpaceWizard.CopyRunner copy, OperationLog log)
    {
        if (!plan.IsPossible) throw new ArgumentException("The plan is not possible.", nameof(plan));
        GiveBytes = PlanLimits.AlignDown(Math.Clamp(giveBytes, plan.MinGive, plan.MaxGive));
        (_inventory, _runner, _limits, _rootOf, _copy, _log) = (inventory, runner, limits, rootOf, copy, log);
        _disk = plan.Disk.Number;
        _diskId = plan.Disk.UniqueId;
        _giverOffset = plan.Giver.OffsetBytes;
        _giverNewSize = PlanLimits.AlignDown(plan.Giver.SizeBytes - GiveBytes);
        _receiverOffset = plan.Receiver.OffsetBytes;
        _letter = plan.Receiver.Volume!.DriveLetter!;
        _label = plan.Receiver.Volume.Label ?? "";
        _tempOffset = plan.TempOffset;
        _needed = plan.NeededForFiles;
        TargetName = plan.Receiver.DisplayName;
        var giverName = plan.Giver.DisplayName;

        Steps =
        [
            new(GiveSpaceStepKind.ShrinkGiver, $"Shrink {giverName} by {ByteSize.Format(GiveBytes)}"),
            new(GiveSpaceStepKind.ShrinkReceiver, $"Shrink {_letter}: so there is room at its end for a copy of its files"),
            new(GiveSpaceStepKind.CreateTemporary, $"Create a temporary volume at the end of {_letter}:'s space"),
            new(GiveSpaceStepKind.CopyAndVerify, $"Copy {_letter}:'s files to it and verify every checksum"),
            new(GiveSpaceStepKind.DeleteDonor, $"Delete the old {_letter}: (only after the copy is verified)"),
            new(GiveSpaceStepKind.CreateNewVolume, $"Create the new, bigger {_letter}: starting right after {giverName}"),
            new(GiveSpaceStepKind.CopyBack, $"Copy the files back into it and verify every checksum"),
            new(GiveSpaceStepKind.DeleteTemporary, "Delete the temporary volume (only after the copy back is verified)"),
            new(GiveSpaceStepKind.ExtendTarget, $"Extend the new {_letter}: over the rest of the space"),
            new(GiveSpaceStepKind.RenameTemporary, $"Give the new volume the letter {_letter}:"),
        ];
        if (plan.Receiver.EndBytes <= _tempOffset)
        {
            var shrink = Step(GiveSpaceStepKind.ShrinkReceiver)!;
            shrink.State = StepState.Skipped;
            shrink.Message = $"Not needed: there is free space after {_letter}: for the copy.";
        }
    }

    public string TargetName { get; }
    public long GiveBytes { get; }
    public IReadOnlyList<GiveSpaceStep> Steps { get; }
    public GiveSpaceStep? NextStep => Steps.FirstOrDefault(s => s.State is StepState.Pending or StepState.Failed);
    public bool IsFinished => NextStep is null;

    private GiveSpaceStep? Step(GiveSpaceStepKind kind) => Steps.FirstOrDefault(s => s.Kind == kind);
    private void Say(GiveSpaceStepKind kind, string message) { if (Step(kind) is { } s) s.Message = message; }

    public async Task<GiveSpaceStep?> RunNextAsync(Func<string, bool> confirmDelete, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        var step = NextStep;
        if (step is null) return null;
        _log.Info($"Take space: step “{step.Title}”.");
        string? failure;
        try
        {
            failure = step.Kind switch
            {
                GiveSpaceStepKind.ShrinkGiver => await ShrinkGiverAsync(ct),
                GiveSpaceStepKind.ShrinkReceiver => await ShrinkReceiverAsync(ct),
                GiveSpaceStepKind.CreateTemporary => await CreateTemporaryAsync(ct),
                GiveSpaceStepKind.CopyAndVerify => await CopyAsync(_letter, _tempLetter, GiveSpaceStepKind.CopyAndVerify, progress, ct),
                GiveSpaceStepKind.DeleteDonor => await DeleteAsync(_receiverOffset, _letter, GiveSpaceStepKind.DeleteDonor, confirmDelete, ct),
                GiveSpaceStepKind.CreateNewVolume => await CreateNewAsync(ct),
                GiveSpaceStepKind.CopyBack => await CopyAsync(_tempLetter, _newLetter, GiveSpaceStepKind.CopyBack, progress, ct),
                GiveSpaceStepKind.DeleteTemporary => await DeleteAsync(_tempAt, _tempLetter, GiveSpaceStepKind.DeleteTemporary, confirmDelete, ct),
                GiveSpaceStepKind.ExtendTarget => await ExtendAsync(ct),
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
            _log.Error($"Take space stopped at “{step.Title}”: {failure}");
        }
        return step;
    }

    private const string NotConfirmed = "\u0000not-confirmed";

    private async Task<(PhysicalDisk Disk, DiskLayout Layout)> ReadAsync(CancellationToken ct)
    {
        var snapshot = await _inventory.GetSnapshotAsync(ct);
        var disk = snapshot.Disks.FirstOrDefault(d => d.Number == _disk && d.UniqueId == _diskId)
                   ?? throw new InvalidOperationException("The disk is no longer the same disk.");
        return (disk, DiskLayoutBuilder.Build(disk));
    }

    private static Partition? At(PhysicalDisk disk, long offset) => disk.Partitions.FirstOrDefault(p => p.OffsetBytes == offset);

    private async Task<string?> Shrink(GiveSpaceStepKind kind, long offset, long newSize, string what, CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var part = At(disk, offset);
        if (part is null) return $"{what} is not where it was expected.";
        if (part.SizeBytes <= newSize)
        {
            Say(kind, "Already small enough.");
            return null;
        }
        var outcome = await _runner.ResizeAsync(new ResizeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk),
            part.PartitionNumber, part.OffsetBytes, part.SizeBytes, newSize), ct);
        Say(kind, outcome.Message);
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    private Task<string?> ShrinkGiverAsync(CancellationToken ct) =>
        Shrink(GiveSpaceStepKind.ShrinkGiver, _giverOffset, _giverNewSize, "The volume giving space", ct);

    private Task<string?> ShrinkReceiverAsync(CancellationToken ct) =>
        Shrink(GiveSpaceStepKind.ShrinkReceiver, _receiverOffset, PlanLimits.AlignDown(_tempOffset - _receiverOffset), $"{_letter}:", ct);

    private async Task<(string? Failure, string? Letter, long Offset)> CreateIn(long fromOffset, long maxEnd, string label, CancellationToken ct)
    {
        var (disk, layout) = await ReadAsync(ct);
        var free = layout.Regions.FirstOrDefault(r => r.Kind == RegionKind.Unallocated && r.EndBytes > fromOffset && r.OffsetBytes <= fromOffset + PlanLimits.Alignment);
        if (free is null) return ("The free space for the new volume is not where it was expected.", null, -1);
        var (start, limits) = PlanLimits.ForNewVolume(layout, free);
        var offset = Math.Max(start, fromOffset);
        var size = PlanLimits.AlignDown(Math.Min(start + limits.MaxBytes, maxEnd) - offset);
        if (size < _needed)
            return ($"Only {ByteSize.Format(size)} is available, but the files need about {ByteSize.Format(_needed)}.", null, -1);
        var outcome = await _runner.CreateVolumeAsync(new CreateVolumeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk), offset, size, label), ct);
        if (outcome.Status != OperationStatus.Succeeded) return (outcome.Message, null, -1);
        var letter = outcome.StateAfter?.Disks.FirstOrDefault(d => d.Number == _disk)?.Partitions.FirstOrDefault(p => p.OffsetBytes == offset)?.Volume?.DriveLetter;
        return letter is null
            ? ("The volume was created but has no drive letter, so files can't be copied to it.", null, offset)
            : (null, letter, offset);
    }

    private async Task<string?> CreateTemporaryAsync(CancellationToken ct)
    {
        var (failure, letter, offset) = await CreateIn(_tempOffset, long.MaxValue, _label, ct);
        _tempLetter = letter;
        _tempAt = offset;
        Say(GiveSpaceStepKind.CreateTemporary, failure ?? $"Created the temporary volume ({letter}:).");
        return failure;
    }

    private async Task<string?> CreateNewAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var giver = At(disk, _giverOffset);
        if (giver is null) return "The volume that gave space is not where it was expected.";
        var start = PlanLimits.AlignUp(giver.EndBytes);
        var (failure, letter, offset) = await CreateIn(start, _tempAt > 0 ? _tempAt : _tempOffset, _label, ct);
        _newLetter = letter;
        _newOffset = offset;
        Say(GiveSpaceStepKind.CreateNewVolume, failure ?? $"Created the new volume ({letter}:) right after the volume that gave space.");
        return failure;
    }

    private async Task<string?> CopyAsync(string? fromLetter, string? toLetter, GiveSpaceStepKind kind, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        if (fromLetter is null || toLetter is null) return "A volume for this step is not known. Run the previous steps first.";
        var source = _rootOf(fromLetter);
        var destination = _rootOf(toLetter);
        var resume = VerifiedCopy.IsUnfinishedCopyOf(destination, source);
        var report = await _copy(source, destination, resume, progress, ct);
        Say(kind, report.Message);
        return report.IsVerified ? null : report.Message;
    }

    private async Task<string?> DeleteAsync(long offset, string? letter, GiveSpaceStepKind kind, Func<string, bool> confirm, CancellationToken ct)
    {
        if (letter is null) return "The volume to delete is not known.";
        var (disk, _) = await ReadAsync(ct);
        var part = At(disk, offset);
        if (part?.Volume?.DriveLetter != letter) return $"{letter}: is not where it was expected.";
        if (!confirm($"DELETE {letter}")) return NotConfirmed;
        var root = _rootOf(letter);
        var outcome = await _runner.DeleteAsync(
            new DeleteOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk), part.PartitionNumber, part.OffsetBytes, part.SizeBytes),
            _ => DeleteSafety.WhyNotSafeToDelete(root, VerifiedCopyRegistry.Find(root), ct), ct);
        Say(kind, outcome.Message);
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    private async Task<string?> ExtendAsync(CancellationToken ct)
    {
        var (disk, layout) = await ReadAsync(ct);
        var target = At(disk, _newOffset);
        if (target is null) return "The new volume is not where it was expected.";
        var limits = await _limits.GetLimitsAsync(layout, target, PlanKind.ExtendVolume, ct);
        if (!limits.IsUsable || limits.MaxBytes <= target.SizeBytes)
        {
            Say(GiveSpaceStepKind.ExtendTarget, "Nothing left to extend into.");
            return null;
        }
        var outcome = await _runner.ResizeAsync(new ResizeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk),
            target.PartitionNumber, target.OffsetBytes, target.SizeBytes, limits.MaxBytes), ct);
        Say(GiveSpaceStepKind.ExtendTarget, outcome.Message);
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    private async Task<string?> RenameAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var target = At(disk, _newOffset);
        if (target?.Volume is null) return "The new volume is not where it was expected.";
        if (string.Equals(target.Volume.DriveLetter, _letter, StringComparison.OrdinalIgnoreCase))
        {
            Say(GiveSpaceStepKind.RenameTemporary, $"It already has the letter {_letter}:.");
            return null;
        }
        var outcome = await _runner.ChangeDriveLetterAsync(new DriveLetterOrder(disk.Number, disk.UniqueId, target.PartitionNumber,
            target.OffsetBytes, target.Volume.DriveLetter, _letter), ct);
        Say(GiveSpaceStepKind.RenameTemporary, outcome.Message);
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }
}
