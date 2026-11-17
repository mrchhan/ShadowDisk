using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.Core.Moving;

public enum SectorStepKind { Shrink, Move, Extend }

/// <summary>
/// One step of a sector-to-sector change. The partition is found by where it is when the step starts
/// (<paramref name="Offset"/>, <paramref name="Size"/>), because a move changes its start.
/// </summary>
public sealed record SectorStep(SectorStepKind Kind, string Name, long Offset, long Size, long NewOffset, long NewSize)
{
    public string Text => Kind switch
    {
        SectorStepKind.Shrink => $"Shrink {Name} from {ByteSize.Format(Size)} to {ByteSize.Format(NewSize)} (Windows moves nothing; the end is cut off).",
        SectorStepKind.Move => $"Move {Name} {(NewOffset < Offset ? "left" : "right")} by {ByteSize.Format(Math.Abs(NewOffset - Offset))}, sector to sector.",
        _ => $"Extend {Name} from {ByteSize.Format(Size)} to {ByteSize.Format(NewSize)}.",
    };
}

/// <summary>A planned sector-to-sector change: what happens in which order, or why it can't.</summary>
public sealed record SectorPlan(IReadOnlyList<SectorStep> Steps, IReadOnlyList<string> Problems, string Summary, long MovedUsedBytes)
{
    public bool IsPossible => Problems.Count == 0 && Steps.Count > 0;
    public SectorStep? MoveStep => Steps.FirstOrDefault(s => s.Kind == SectorStepKind.Move);
}

/// <summary>
/// Plans MiniTool-style changes made by moving whole blocks instead of copying files:
/// • moving one drive into the free space next to it (optionally taking that space in);
/// • dragging the divider between two drives (shrink one, move the right one, extend the other).
/// The first drive and the Windows drive never move (see <see cref="MovePlanner.WhyLocked"/>).
/// </summary>
public static class SectorPlanner
{
    /// <summary>Smaller changes are refused: Windows won't extend into less than this.</summary>
    public const long MinimumChange = Safety.SafetyAnalyzer.MinimumUsefulSpace;

    private static SectorPlan No(params string[] problems) => new([], problems, "", 0);

    /// <summary>
    /// Move <paramref name="p"/> to <paramref name="newOffset"/>. With <paramref name="takeInFreedSpace"/> and a move to the left,
    /// the drive afterwards grows into the space it left behind, so it "takes in" the free space in front of it.
    /// </summary>
    public static SectorPlan ForMove(DiskLayout layout, Partition p, long newOffset, bool takeInFreedSpace)
    {
        var range = MovePlanner.Range(layout, p);
        if (range.Locked is { } locked) return No(locked);
        newOffset = PlanLimits.AlignDown(newOffset);
        if (newOffset == p.OffsetBytes) return No("Choose a new position first.");
        if (newOffset < range.MinOffset || newOffset > range.MaxOffset) return No("The new position is outside the free space next to the drive.");

        var steps = new List<SectorStep> { new(SectorStepKind.Move, p.DisplayName, p.OffsetBytes, p.SizeBytes, newOffset, p.SizeBytes) };
        var summary = $"{p.DisplayName} moves {(newOffset < p.OffsetBytes ? "left" : "right")} by {ByteSize.Format(Math.Abs(newOffset - p.OffsetBytes))}.";
        if (takeInFreedSpace && p.OffsetBytes - newOffset >= MinimumChange)
        {
            var newSize = p.SizeBytes + (p.OffsetBytes - newOffset);
            steps.Add(new(SectorStepKind.Extend, p.DisplayName, newOffset, p.SizeBytes, newOffset, newSize));
            summary = $"{p.DisplayName} takes in the {ByteSize.Format(p.OffsetBytes - newOffset)} of free space in front of it and becomes {ByteSize.Format(newSize)}.";
        }
        return new(steps, [], summary, p.Volume?.UsedBytes ?? p.SizeBytes);
    }

    /// <summary>The divider between neighbours <paramref name="left"/> and <paramref name="right"/> dragged to <paramref name="newBoundary"/>.</summary>
    public static SectorPlan ForBoundary(DiskLayout layout, Partition left, Partition right, long newBoundary)
    {
        if (left.Volume is null || right.Volume is null) return No("Both sides must be drives with files.");
        var regions = layout.Regions.Where(r => r.Kind != RegionKind.Gap).ToList();
        var i = regions.FindIndex(r => ReferenceEquals(r.Partition, left));
        if (i < 0 || i + 1 >= regions.Count || !ReferenceEquals(regions[i + 1].Partition, right))
            return No("The two drives are not next to each other.");
        if (MovePlanner.WhyLocked(layout.Disk, right) is { } locked)
            return No(locked + " The drive on the right is the one that moves, so this can't be done sector to sector.");

        var gap = right.OffsetBytes - left.EndBytes;
        var raw = newBoundary - left.EndBytes;
        var delta = Math.Sign(raw) * PlanLimits.AlignDown(Math.Abs(raw));
        if (Math.Abs(delta) < MinimumChange) return No($"Drag the divider at least {ByteSize.Format(MinimumChange)}.");
        var problems = new List<string>();

        if (delta > 0)
        {
            // Right gives: cut its end, move it right, then the left drive grows into the space.
            var rightSize = right.SizeBytes - delta;
            if (rightSize < MinSize(right)) problems.Add($"{right.DisplayName} can't get smaller than about {ByteSize.Format(MinSize(right))}: its files need the room.");
            if (problems.Count > 0) return new([], problems, "", 0);
            return new(
            [
                new(SectorStepKind.Shrink, right.DisplayName, right.OffsetBytes, right.SizeBytes, right.OffsetBytes, rightSize),
                new(SectorStepKind.Move, right.DisplayName, right.OffsetBytes, rightSize, right.OffsetBytes + delta, rightSize),
                new(SectorStepKind.Extend, left.DisplayName, left.OffsetBytes, left.SizeBytes, left.OffsetBytes, left.SizeBytes + delta),
            ], [], $"{right.DisplayName} gives {ByteSize.Format(delta)} to {left.DisplayName}.", right.Volume.UsedBytes);
        }

        // Left gives: cut its end, move the right drive left, then it grows into the space behind it.
        var give = -delta;
        if (left.IsBoot) return No($"{left.DisplayName} is the drive Windows runs from. It can only get bigger, never smaller.");
        var leftSize = left.SizeBytes - give;
        if (leftSize < MinSize(left)) problems.Add($"{left.DisplayName} can't get smaller than about {ByteSize.Format(MinSize(left))}: its files need the room.");
        if (problems.Count > 0) return new([], problems, "", 0);
        var newRightOffset = left.OffsetBytes + leftSize + gap;
        return new(
        [
            new(SectorStepKind.Shrink, left.DisplayName, left.OffsetBytes, left.SizeBytes, left.OffsetBytes, leftSize),
            new(SectorStepKind.Move, right.DisplayName, right.OffsetBytes, right.SizeBytes, newRightOffset, right.SizeBytes),
            new(SectorStepKind.Extend, right.DisplayName, newRightOffset, right.SizeBytes, newRightOffset, right.SizeBytes + give),
        ], [], $"{left.DisplayName} gives {ByteSize.Format(give)} to {right.DisplayName}.", right.Volume.UsedBytes);
    }

    /// <summary>A rough lower size limit (files plus a margin); Windows' own limit is checked again before the shrink.</summary>
    public static long MinSize(Partition p)
    {
        var used = p.Volume?.UsedBytes ?? p.SizeBytes;
        return PlanLimits.AlignUp(used + Math.Max(used / 20, ByteSize.MiB));
    }
}

/// <summary>Where a sector-to-sector change is up to.</summary>
public sealed record SectorProgress(int StepIndex, int StepCount, SectorStep Step, MoveProgress? Move);

/// <summary>
/// Runs a <see cref="SectorPlan"/> step by step. Each step re-reads the disk and checks everything again (the runner and
/// mover do that). It stops at the first step that does not succeed and says which steps are done.
/// </summary>
public sealed class SectorRebalanceRunner(IStorageInventory inventory, OperationRunner runner, PartitionMover mover, OperationLog log)
{
    public async Task<OperationOutcome> RunAsync(int diskNumber, string? diskUniqueId, SectorPlan plan, IProgress<SectorProgress>? progress, CancellationToken ct)
    {
        var steps = new List<string>();
        InventorySnapshot? last = null;

        // Before step 1: make sure the drive that moves really can be moved (lock + used-space map), so nothing is shrunk for nothing.
        if (plan.MoveStep is { } moveStep)
        {
            InventorySnapshot first;
            try { first = await inventory.GetSnapshotAsync(ct); }
            catch (OperationCanceledException) { return new(OperationStatus.Cancelled, "Cancelled. Nothing was changed.", steps, null); }
            var disk0 = first.Disks.FirstOrDefault(d => d.Number == diskNumber && d.UniqueId == diskUniqueId);
            var moving = disk0?.Partitions.FirstOrDefault(x => x.OffsetBytes == moveStep.Offset);
            if (disk0 is null || moving is null)
                return new(OperationStatus.Blocked, $"{moveStep.Name} is not where the plan expected it. Nothing was changed.", steps, null);
            progress?.Report(new SectorProgress(0, plan.Steps.Count, plan.Steps[0], null));
            if (await mover.PreflightAsync(disk0, moving, ct) is { } why)
                return new(OperationStatus.Blocked, $"{moveStep.Name} can't be moved right now: {why} Nothing was changed.", ["Checked before starting: " + why], null);
            steps.Add($"Checked before starting: {moveStep.Name} can be locked and its used-space map read.");
        }
        for (var k = 0; k < plan.Steps.Count; k++)
        {
            var step = plan.Steps[k];
            progress?.Report(new SectorProgress(k, plan.Steps.Count, step, null));
            log.Info($"Sector change, step {k + 1} of {plan.Steps.Count}: {step.Text}");

            InventorySnapshot snapshot;
            try { snapshot = await inventory.GetSnapshotAsync(ct); }
            catch (OperationCanceledException) { return Stopped(k, OperationStatus.Cancelled, "Cancelled."); }
            var disk = snapshot.Disks.FirstOrDefault(d => d.Number == diskNumber && d.UniqueId == diskUniqueId);
            var p = disk?.Partitions.FirstOrDefault(x => x.OffsetBytes == step.Offset && x.SizeBytes == step.Size);
            if (disk is null || p is null)
                return Stopped(k, OperationStatus.Blocked, $"{step.Name} is not where step {k + 1} expected it. Nothing more was changed.");

            var fingerprint = LayoutFingerprint.Of(disk);
            var outcome = step.Kind switch
            {
                SectorStepKind.Move => await mover.MoveAsync(
                    new MoveOrder(disk.Number, disk.UniqueId, fingerprint, p.PartitionNumber, p.OffsetBytes, p.SizeBytes, step.NewOffset),
                    progress is null ? null : new InlineProgress<MoveProgress>(m => progress.Report(new SectorProgress(k, plan.Steps.Count, step, m))), ct),
                _ => await runner.ResizeAsync(
                    new ResizeOrder(disk.Number, disk.UniqueId, fingerprint, p.PartitionNumber, p.OffsetBytes, p.SizeBytes, step.NewSize), ct),
            };
            steps.Add($"Step {k + 1}: {outcome.Message}");
            last = outcome.StateAfter ?? last;
            if (outcome.Status != OperationStatus.Succeeded) return Stopped(k, outcome.Status, outcome.Message);
        }
        return new(OperationStatus.Succeeded, plan.Summary + " All steps finished and were checked.", steps, last);

        OperationOutcome Stopped(int k, OperationStatus status, string message)
        {
            var doneSteps = string.Join(", ", plan.Steps.Take(k).Select(s => s.Kind.ToString().ToLowerInvariant() + " " + s.Name));
            var done = k == 0 ? "No step had finished, so the disk is as it was"
                : k == 1 ? $"Step 1 ({doneSteps}) is done and stays done"
                : $"Steps 1–{k} ({doneSteps}) are done and stay done";
            var rest = plan.Steps.Skip(k + 1).Select(s => s.Text).ToList();
            var after = rest.Count == 0 ? "" : " Not done yet: " + string.Join(" ", rest);
            // Anything after the first step is a partly-made change; a paused move is resumable and says so itself.
            var overall = k > 0 && status is OperationStatus.Blocked or OperationStatus.Failed or OperationStatus.Cancelled ? OperationStatus.PartiallyApplied : status;
            steps.Add($"Stopped at step {k + 1}.");
            return new(overall, $"{message} {done}.{after}", steps, last);
        }
    }
}

/// <summary>Reports progress immediately on the calling thread.</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
