using ShadowDisk.Core.Copying;
using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.Core.Wizard;

// Stage 5, part 3: "Give space to the drive before".
//
//   Before:  | target | donor (files)             | (free?) |
//   After:   | target + given space        | donor' (same files, same letter) |
//
// Built only from operations already proven on their own: shrink, create volume, copy & verify,
// delete volume, extend, change drive letter. Each step re-reads the disk and is checked again.

public sealed record GiveSpacePlan(
    PhysicalDisk Disk,
    Partition Target,
    Partition? Donor,
    long DonorUsedBytes,
    long NeededForDonorFiles,   // size the new donor volume needs: its files plus headroom
    long AreaBytes,             // donor partition + free space directly after it
    long MinGive,
    long MaxGive,
    IReadOnlyList<string> Problems)
{
    public bool IsPossible => Problems.Count == 0;
}

public static class GiveSpacePlanner
{
    /// <summary>
    /// Works out whether space can be given to <paramref name="target"/> from the volume directly after it,
    /// and how much. <paramref name="donorShrinkLimits"/> are the donor's shrink limits (from Windows if possible).
    /// </summary>
    public static GiveSpacePlan Plan(DiskLayout layout, Partition target, SizeLimits? donorShrinkLimits)
    {
        var problems = new List<string>();
        var regions = layout.Regions.Where(r => r.Kind != RegionKind.Gap).ToList();
        var index = regions.FindIndex(r => ReferenceEquals(r.Partition, target));
        var next = index >= 0 && index + 1 < regions.Count ? regions[index + 1] : null;
        var donor = next?.Partition;

        if (target.IsBoot || target.IsSystem || target.IsStartupPartition)
            problems.Add($"{target.DisplayName} holds Windows or its start-up files. Giving space to the Windows drive comes in a later part of ShadowDisk.");
        else if (target.Kind is not (PartitionKind.Basic or PartitionKind.Logical) || target.Volume?.FileSystem is not ("NTFS" or "REFS"))
            problems.Add($"{target.DisplayName} must be an ordinary NTFS or ReFS data volume to be extended.");

        if (next is null)
            problems.Add($"There is nothing after {target.DisplayName} on this disk to take space from.");
        else if (next.Kind == RegionKind.Unallocated)
            problems.Add($"There is already free space directly after {target.DisplayName}. Use “Plan a change” → Extend instead.");
        else if (donor is not null)
        {
            if (OperationRunner.WhyKindNotDeletable(donor) is { } kindReason)
                problems.Add($"{donor.DisplayName} sits directly after {target.DisplayName}, but it can't be moved: {kindReason}");
            else if (donor.Volume?.DriveLetter is not { Length: > 0 })
                problems.Add($"{donor.DisplayName} has no drive letter, so its files can't be copied. Give it a letter first.");
            else if (donor.Volume.FileSystem is not ("NTFS" or "REFS"))
                problems.Add($"{donor.DisplayName} uses {donor.Volume.FileSystem ?? "an unknown file system"}; this wizard handles NTFS and ReFS volumes.");
        }

        if (donor is null || problems.Count > 0)
            return new GiveSpacePlan(layout.Disk, target, donor, 0, 0, 0, 0, 0, problems);

        var used = donor.Volume!.UsedBytes;
        var needed = Math.Max(PlanLimits.MinimumNewVolume, PlanLimits.AlignUp(used + Math.Max(used / 10, 256 * ByteSize.MiB)));

        // Free space directly after the donor (usable area, aligned).
        var after = regions.Skip(index + 2).FirstOrDefault();
        long areaEnd = donor.EndBytes;
        if (after is { Kind: RegionKind.Unallocated })
        {
            var (start, lim) = PlanLimits.ForNewVolume(layout, after);
            areaEnd = Math.Max(areaEnd, start + lim.MaxBytes);
        }
        else if (layout.Disk.Style == PartitionStyle.Gpt && donor.EndBytes >= layout.Disk.SizeBytes - PlanLimits.GptTailReserve)
        {
            areaEnd = layout.Disk.SizeBytes - PlanLimits.GptTailReserve;
        }
        var area = areaEnd - donor.OffsetBytes;

        // Giving less than the donor's size means shrinking it first, which Windows limits.
        var minGive = donorShrinkLimits is { IsUsable: true } l ? PlanLimits.AlignUp(l.MinBytes) : donor.SizeBytes;
        var maxGive = PlanLimits.AlignDown(area - needed);
        if (maxGive < minGive)
            problems.Add($"{donor.DisplayName} holds {ByteSize.Format(used)}. Its files need about {ByteSize.Format(needed)} of the " +
                         $"{ByteSize.Format(area)} available, which leaves too little to give. Free up space on {donor.DisplayName} or move some files to another drive first.");

        return new GiveSpacePlan(layout.Disk, target, donor, used, needed, area, minGive, maxGive, problems);
    }
}

public enum GiveSpaceStepKind { ShrinkDonor, CreateTemporary, CopyAndVerify, DeleteDonor, ExtendTarget, RenameTemporary }
public enum StepState { Pending, Done, Failed, Skipped }

public sealed class GiveSpaceStep(GiveSpaceStepKind kind, string title)
{
    public GiveSpaceStepKind Kind { get; } = kind;
    public string Title { get; } = title;
    public StepState State { get; internal set; }
    public string? Message { get; internal set; }
}

/// <summary>Runs the give-space steps one at a time. Each call to <see cref="RunNextAsync"/> runs one step.</summary>
public sealed class GiveSpaceWizard
{
    public delegate Task<CopyReport> CopyRunner(string source, string destination, bool resume, IProgress<CopyProgress>? progress, CancellationToken ct);

    private readonly IStorageInventory _inventory;
    private readonly OperationRunner _runner;
    private readonly IPartitionLimitsProvider _limits;
    private readonly Func<string, string> _rootOf;
    private readonly CopyRunner _copy;
    private readonly OperationLog _log;
    private readonly int _disk;
    private readonly string? _diskId;
    private readonly long _targetOffset;
    private readonly long _donorOffset;
    private readonly long _donorSize;
    private readonly string _donorLetter;
    private readonly string _donorLabel;
    private readonly long _give;
    private readonly long _needed;
    private long _tempOffset = -1;
    private string? _tempLetter;

    public GiveSpaceWizard(GiveSpacePlan plan, long giveBytes, IStorageInventory inventory, OperationRunner runner,
        IPartitionLimitsProvider limits, Func<string, string> rootOf, CopyRunner copy, OperationLog log)
    {
        if (!plan.IsPossible || plan.Donor is null) throw new ArgumentException("The plan is not possible.", nameof(plan));
        _give = PlanLimits.AlignDown(Math.Clamp(giveBytes, plan.MinGive, plan.MaxGive));
        (_inventory, _runner, _limits, _rootOf, _copy, _log) = (inventory, runner, limits, rootOf, copy, log);
        _disk = plan.Disk.Number;
        _diskId = plan.Disk.UniqueId;
        _targetOffset = plan.Target.OffsetBytes;
        _donorOffset = plan.Donor.OffsetBytes;
        _donorSize = plan.Donor.SizeBytes;
        _donorLetter = plan.Donor.Volume!.DriveLetter!;
        _donorLabel = plan.Donor.Volume.Label ?? "";
        _needed = plan.NeededForDonorFiles;
        TargetName = plan.Target.DisplayName;

        Steps =
        [
            new(GiveSpaceStepKind.ShrinkDonor, $"Shrink {_donorLetter}: so there is room for a copy of its files"),
            new(GiveSpaceStepKind.CreateTemporary, "Create a temporary volume at the end of the space"),
            new(GiveSpaceStepKind.CopyAndVerify, $"Copy {_donorLetter}:'s files to it and verify every checksum"),
            new(GiveSpaceStepKind.DeleteDonor, $"Delete the old {_donorLetter}: (only after the copy is verified)"),
            new(GiveSpaceStepKind.ExtendTarget, $"Extend {TargetName} into the freed space"),
            new(GiveSpaceStepKind.RenameTemporary, $"Give the new volume the letter {_donorLetter}:"),
        ];
        if (_give >= _donorSize)
        {
            Steps[0].State = StepState.Skipped;
            Steps[0].Message = $"Not needed: the copy fits in the free space after {_donorLetter}:.";
        }
    }

    public string TargetName { get; }
    public long GiveBytes => _give;
    public IReadOnlyList<GiveSpaceStep> Steps { get; }
    public GiveSpaceStep? NextStep => Steps.FirstOrDefault(s => s.State is StepState.Pending or StepState.Failed);
    public bool IsFinished => NextStep is null;

    /// <summary>
    /// Runs the next pending (or failed) step once. <paramref name="confirmDelete"/> is asked before the delete;
    /// returning false leaves that step pending and changes nothing.
    /// </summary>
    public async Task<GiveSpaceStep?> RunNextAsync(Func<string, bool> confirmDelete, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        var step = NextStep;
        if (step is null) return null;
        var earlier = Steps.TakeWhile(s => s != step).FirstOrDefault(s => s.State is StepState.Pending or StepState.Failed);
        if (earlier is not null) return earlier;

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
        if (failure is not null) step.Message = failure + " The wizard has stopped; nothing after this step was done.";
        if (failure is not null) _log.Error($"Give space stopped at “{step.Title}”: {failure}");
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

    private async Task<string?> ShrinkAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var donor = At(disk, _donorOffset);
        if (donor?.Volume?.DriveLetter != _donorLetter) return $"{_donorLetter}: is not where it was expected.";
        var target = PlanLimits.AlignDown(_give);
        if (donor.SizeBytes <= target)
        {
            Steps[0].Message = "Already small enough.";
            return null;
        }
        var outcome = await _runner.ResizeAsync(new ResizeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk),
            donor.PartitionNumber, donor.OffsetBytes, donor.SizeBytes, target), ct);
        Steps[0].Message = outcome.Message;
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    private async Task<string?> CreateAsync(CancellationToken ct)
    {
        var (disk, layout) = await ReadAsync(ct);
        var regions = layout.Regions.Where(r => r.Kind != RegionKind.Gap).ToList();
        var donorIndex = regions.FindIndex(r => r.Partition?.OffsetBytes == _donorOffset);
        var free = donorIndex >= 0 && donorIndex + 1 < regions.Count ? regions[donorIndex + 1] : null;
        if (free is not { Kind: RegionKind.Unallocated }) return $"There is no free space after {_donorLetter}: for the temporary volume.";

        var (start, limits) = PlanLimits.ForNewVolume(layout, free);
        var offset = Math.Max(start, PlanLimits.AlignUp(_donorOffset + _give));
        var size = PlanLimits.AlignDown(start + limits.MaxBytes - offset);
        if (size < _needed)
            return $"Only {ByteSize.Format(size)} is available for the copy, but {_donorLetter}:'s files need about {ByteSize.Format(_needed)}.";

        var outcome = await _runner.CreateVolumeAsync(new CreateVolumeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk),
            offset, size, _donorLabel), ct);
        Steps[1].Message = outcome.Message;
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
        Steps[2].Message = report.Message;
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
        Steps[3].Message = outcome.Message;
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    private async Task<string?> ExtendAsync(CancellationToken ct)
    {
        var (disk, layout) = await ReadAsync(ct);
        var target = At(disk, _targetOffset);
        if (target is null) return $"{TargetName} is not where it was expected.";
        var limits = await _limits.GetLimitsAsync(layout, target, PlanKind.ExtendVolume, ct);
        if (!limits.IsUsable) return $"There is no free space directly after {TargetName} to extend into.";
        var outcome = await _runner.ResizeAsync(new ResizeOrder(disk.Number, disk.UniqueId, LayoutFingerprint.Of(disk),
            target.PartitionNumber, target.OffsetBytes, target.SizeBytes, limits.MaxBytes), ct);
        Steps[4].Message = outcome.Message;
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }

    private async Task<string?> RenameAsync(CancellationToken ct)
    {
        var (disk, _) = await ReadAsync(ct);
        var temp = At(disk, _tempOffset);
        if (temp?.Volume is null) return "The new volume is not where it was expected.";
        if (string.Equals(temp.Volume.DriveLetter, _donorLetter, StringComparison.OrdinalIgnoreCase))
        {
            Steps[5].Message = $"It already has the letter {_donorLetter}:.";
            return null;
        }
        var outcome = await _runner.ChangeDriveLetterAsync(new DriveLetterOrder(disk.Number, disk.UniqueId, temp.PartitionNumber,
            temp.OffsetBytes, temp.Volume.DriveLetter, _donorLetter), ct);
        Steps[5].Message = outcome.Message;
        return outcome.Status == OperationStatus.Succeeded ? null : outcome.Message;
    }
}
