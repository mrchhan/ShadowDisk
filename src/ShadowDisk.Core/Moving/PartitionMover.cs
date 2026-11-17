using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.Core.Moving;

/// <summary>The operating-system side of a move (Windows: volume lock, NTFS map, partition table).</summary>
public interface IMoveHost
{
    /// <summary>
    /// Takes the volume offline for the move: locks it (fails if files are open), and, when <paramref name="readUsedMap"/>,
    /// returns NTFS's map of used space as ranges relative to the partition start. Then dismounts it.
    /// When resuming, the volume is already hidden and this does nothing.
    /// </summary>
    Task<(IReadOnlyList<ByteRange>? Used, string? Error)> TakeOfflineAsync(MoveJournal j, bool readUsedMap, CancellationToken ct);

    /// <summary>
    /// A dry run of <see cref="TakeOfflineAsync"/>: checks the volume can be locked and its used-space map read, then lets go.
    /// Changes nothing. Returns why not, or null.
    /// </summary>
    Task<string?> CheckAsync(MoveJournal j, CancellationToken ct);

    /// <summary>Marks the partition "ShadowDisk: moving" so Windows won't mount or check it. Remembers the original type in the journal first. Safe to repeat.</summary>
    Task<BackendResult> HideAsync(MoveJournal j, MoveJournalStore store, CancellationToken ct);

    /// <summary>Opens raw access to the disk, limited to the move's area.</summary>
    IMoveDevice OpenDevice(MoveJournal j);

    /// <summary>Writes the new start and the original type to the partition table in one write, lets Windows mount it and restores its letter. Safe to repeat.</summary>
    Task<BackendResult> FinishAsync(MoveJournal j, CancellationToken ct);

    /// <summary>Lets go of the volume lock (if still held).</summary>
    void Release();
}

/// <summary>A planned move of one partition to <paramref name="NewOffsetBytes"/>.</summary>
public sealed record MoveOrder(int DiskNumber, string? DiskUniqueId, string LayoutFingerprint, int PartitionNumber,
    long OffsetBytes, long SizeBytes, long NewOffsetBytes);

/// <summary>Where a partition may move to, or why it is locked in place.</summary>
public sealed record MoveRange(long MinOffset, long MaxOffset, string? Locked)
{
    public bool CanMove => Locked is null && MaxOffset > MinOffset;
}

public static class MovePlanner
{
    /// <summary>Why this partition is locked in place (it never moves), or null.</summary>
    public static string? WhyLocked(PhysicalDisk disk, Partition p)
    {
        if (p.IsBoot) return $"{p.DisplayName} is the drive Windows runs from. It is locked in place.";
        if (p.IsSystem || p.IsStartupPartition || p.Kind is not (PartitionKind.Basic))
            return $"{p.DisplayName} is a system, start-up, recovery or reserved partition. It is locked in place.";
        if (disk.Style == PartitionStyle.Mbr && disk.Partitions.Any(x => x.Kind is PartitionKind.Extended or PartitionKind.Logical))
            return "Disks with an extended partition and logical drives are not supported for moves yet.";
        if (p.Volume is null || p.Volume.DriveLetter is not { Length: > 0 })
            return $"{p.DisplayName} needs a drive letter to be moved.";
        if (p.Volume.FileSystem != "NTFS")
            return $"{p.DisplayName} uses {p.Volume.FileSystem ?? "an unknown file system"}. Sector-to-sector moves handle NTFS only.";
        if (p.Volume.Encryption != EncryptionState.NotEncrypted)
            return $"{p.DisplayName} is BitLocker-encrypted (or its state is unknown). It is locked in place; turn BitLocker off first to move it.";
        if (p.Volume.Health != ReportedHealth.Healthy)
            return $"Windows does not report {p.DisplayName} as healthy. Check it before moving.";
        return null;
    }

    /// <summary>How far the partition can move: into the unallocated space directly before and after it.</summary>
    public static MoveRange Range(DiskLayout layout, Partition p)
    {
        if (WhyLocked(layout.Disk, p) is { } locked) return new MoveRange(p.OffsetBytes, p.OffsetBytes, locked);
        var regions = layout.Regions.Where(r => r.Kind != RegionKind.Gap).ToList();
        var i = regions.FindIndex(r => ReferenceEquals(r.Partition, p));
        var before = i > 0 && regions[i - 1].Kind == RegionKind.Unallocated ? regions[i - 1] : null;
        var after = i >= 0 && i + 1 < regions.Count && regions[i + 1].Kind == RegionKind.Unallocated ? regions[i + 1] : null;

        var min = before is null ? p.OffsetBytes : Math.Max(PlanLimits.AlignUp(before.OffsetBytes), PlanLimits.Alignment);
        var maxEnd = after is null ? p.EndBytes : Math.Min(after.EndBytes, layout.Disk.SizeBytes - PlanLimits.TailReserve(layout.Disk));
        var max = Math.Max(p.OffsetBytes, PlanLimits.AlignDown(maxEnd - p.SizeBytes));
        min = Math.Min(min, p.OffsetBytes);
        return new MoveRange(min, max, max > min ? null : $"There is no free space next to {p.DisplayName} to move it into.");
    }
}

/// <summary>Rough time estimates, from a measured read speed of the disk (MiB/s).</summary>
public static class MoveEstimate
{
    /// <summary>Sector to sector: each used byte is read, written and read back once, on the same disk.</summary>
    public static TimeSpan SectorMove(long usedBytes, double mibPerSecond) => Of(usedBytes * 3.2, mibPerSecond);

    /// <summary>Verified file copy: read and write each file, then read both again to compare; files are slower than raw blocks.</summary>
    public static TimeSpan DataCopy(long usedBytes, double mibPerSecond, int copies = 1) => Of(usedBytes * 6.0 * copies, mibPerSecond);

    private static TimeSpan Of(double bytes, double mibPerSecond) =>
        TimeSpan.FromSeconds(Math.Max(5, bytes / (Math.Max(1, mibPerSecond) * ByteSize.MiB)));

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"about {(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalMinutes >= 1 ? $"about {Math.Ceiling(t.TotalMinutes)} min"
        : "under a minute";
}

/// <summary>
/// Moves one partition sector to sector, with the journal for power-loss protection. One attempt, no silent retries;
/// every check is repeated against a fresh read of the disk before anything changes.
/// </summary>
public sealed class PartitionMover(IStorageInventory inventory, IMoveHost host, OperationPolicy policy, OperationLog log,
    MoveJournalStore store, Func<bool?>? onMainsPower = null)
{
    public async Task<OperationOutcome> MoveAsync(MoveOrder order, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        var steps = new List<string>();
        void Step(string s) { steps.Add(s); log.Info($"Move: {s}"); }
        OperationOutcome Stop(OperationStatus status, string message) { log.Warning($"Move {status}: {message}"); return new(status, message, steps, null); }

        if (order.NewOffsetBytes % PlanLimits.Alignment != 0 || order.NewOffsetBytes == order.OffsetBytes)
            return Stop(OperationStatus.Blocked, "The new position is not valid. Nothing was changed.");

        InventorySnapshot before;
        try { before = await inventory.GetSnapshotAsync(ct); }
        catch (OperationCanceledException) { return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed."); }
        var disk = before.Disks.FirstOrDefault(d => d.Number == order.DiskNumber);
        if (disk is null || disk.UniqueId != order.DiskUniqueId)
            return Stop(OperationStatus.Blocked, $"Disk {order.DiskNumber} is no longer the disk that was chosen. Nothing was changed.");
        if (LayoutFingerprint.Of(disk) != order.LayoutFingerprint)
            return Stop(OperationStatus.Blocked, "The disk's layout changed in the meantime. Nothing was changed.");
        var p = disk.Partitions.FirstOrDefault(x => x.OffsetBytes == order.OffsetBytes && x.SizeBytes == order.SizeBytes);
        if (p is null) return Stop(OperationStatus.Blocked, "The partition is not where it was. Nothing was changed.");
        var range = MovePlanner.Range(DiskLayoutBuilder.Build(disk), p);
        if (range.Locked is { } locked) return Stop(OperationStatus.Blocked, locked + " Nothing was changed.");
        if (order.NewOffsetBytes < range.MinOffset || order.NewOffsetBytes > range.MaxOffset)
            return Stop(OperationStatus.Blocked, "The new position is outside the free space next to the partition. Nothing was changed.");
        var notAllowed = policy.WhyNotAllowed(disk, p, onMainsPower?.Invoke());
        if (notAllowed.Count > 0) return Stop(OperationStatus.Blocked, string.Join(" ", notAllowed) + " Nothing was changed.");
        if (ct.IsCancellationRequested) return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed.");
        Step("Confirmed the same disk and layout, and that the new place is free.");

        var j = new MoveJournal
        {
            DiskNumber = disk.Number, DiskUniqueId = disk.UniqueId, DiskLocation = disk.Location, Style = disk.Style,
            SectorSize = Math.Max(512, disk.LogicalSectorSize), OldOffset = p.OffsetBytes, NewOffset = order.NewOffsetBytes,
            SizeBytes = p.SizeBytes, DriveLetter = p.Volume!.DriveLetter, Label = p.Volume.Label,
        };
        j.ChunkBytes = MoveEngine.ChunkSize(Math.Abs(j.NewOffset - j.OldOffset), j.SectorSize);

        Step($"Locking {p.DisplayName} and reading NTFS's map of used space…");
        var (used, error) = await host.TakeOfflineAsync(j, readUsedMap: true, ct);
        if (used is null)
        {
            host.Release();
            return Stop(OperationStatus.Blocked, (error ?? "The volume could not be locked.") + " Nothing was changed.");
        }
        var ranges = MoveEngine.Normalize(used, j.SizeBytes, j.SectorSize);
        j.Ranges = ranges.Select(r => new[] { r.Offset, r.Length }).ToList();
        j.UsedBytes = ranges.Sum(r => r.Length);
        j.Phase = MovePhase.Hiding;
        store.Save(j);
        Step($"Journal saved. {ByteSize.Format(j.UsedBytes)} of used data will move.");
        return await ContinueAsync(j, steps, progress, ct);
    }

    /// <summary>
    /// Checks, without changing anything, that <paramref name="p"/> could be moved right now: rules, policy, and that Windows
    /// lets ShadowDisk lock it and read its used-space map. Used before a multi-step plan starts, so a plan never shrinks a
    /// drive and then finds it can't move the other one.
    /// </summary>
    public async Task<string?> PreflightAsync(PhysicalDisk disk, Partition p, CancellationToken ct)
    {
        if (MovePlanner.WhyLocked(disk, p) is { } locked) return locked;
        var notAllowed = policy.WhyNotAllowed(disk, p, onMainsPower?.Invoke());
        if (notAllowed.Count > 0) return string.Join(" ", notAllowed);
        var j = new MoveJournal
        {
            DiskNumber = disk.Number, DiskUniqueId = disk.UniqueId, Style = disk.Style, SectorSize = Math.Max(512, disk.LogicalSectorSize),
            OldOffset = p.OffsetBytes, NewOffset = p.OffsetBytes, SizeBytes = p.SizeBytes, DriveLetter = p.Volume?.DriveLetter,
        };
        log.Info($"Move: checking that {p.DisplayName} can be locked and read (dry run, nothing is changed)…");
        var error = await host.CheckAsync(j, ct);
        log.Info(error is null ? $"Move: {p.DisplayName} passed the check." : $"Move: {p.DisplayName} failed the check: {error}");
        return error;
    }

    /// <summary>Continues an interrupted move from its journal.</summary>
    public async Task<OperationOutcome> ResumeAsync(MoveJournal j, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        var steps = new List<string>();
        log.Info($"Move: resuming — {j.Describe()} ({j.Phase}, piece {j.NextChunk}).");
        var snapshot = await inventory.GetSnapshotAsync(ct);
        var disk = snapshot.Disks.FirstOrDefault(d => d.UniqueId == j.DiskUniqueId);
        if (disk is null)
            return new(OperationStatus.Blocked, "The disk of this move is not connected. Connect it and try again; the journal is kept.", steps, null);
        j.DiskNumber = disk.Number;
        var (_, error) = await host.TakeOfflineAsync(j, readUsedMap: false, ct);
        if (error is not null)
        {
            host.Release();
            return new(OperationStatus.Blocked, error + " The journal is kept.", steps, null);
        }
        return await ContinueAsync(j, steps, progress, ct);
    }

    private async Task<OperationOutcome> ContinueAsync(MoveJournal j, List<string> steps, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        void Step(string s) { steps.Add(s); log.Info($"Move: {s}"); }
        try
        {
            if (j.Phase == MovePhase.Hiding)
            {
                var hide = await host.HideAsync(j, store, ct);
                if (!hide.Done)
                {
                    store.Delete(j);
                    return new(OperationStatus.Failed, $"The partition could not be prepared for the move ({hide.Error}). Its data was not touched.", steps, null);
                }
                j.Phase = MovePhase.Copying;
                store.Save(j);
                Step("The partition is hidden from Windows while it moves.");
            }

            if (j.Phase == MovePhase.Copying)
            {
                CopyOutcome outcome;
                string message;
                using (var device = host.OpenDevice(j))
                {
                    (outcome, message) = await Task.Run(() => MoveEngine.Copy(j, device, store, progress, ct), CancellationToken.None);
                    if (outcome == CopyOutcome.Done) MoveEngine.PatchBootSectors(j, device);
                }
                Step(message);
                if (outcome == CopyOutcome.Cancelled)
                    return new(OperationStatus.Cancelled, message + " Use “Resume move” to finish it.", steps, null);
                if (outcome == CopyOutcome.Failed)
                    return new(OperationStatus.Failed, message, steps, null);
                j.Phase = MovePhase.Finishing;
                store.Save(j);
            }

            var finish = await host.FinishAsync(j, ct);
            if (!finish.Done)
                return new(OperationStatus.PartiallyApplied,
                    $"The data has moved, but the partition table could not be updated yet ({finish.Error}). Use “Resume move” to finish it; the journal is kept.",
                    steps, null);
            Step("The partition table now points to the new place, and Windows has the volume again.");
            store.Delete(j);

            var after = await inventory.GetSnapshotAsync(CancellationToken.None);
            var moved = after.Disks.FirstOrDefault(d => d.UniqueId == j.DiskUniqueId)?.Partitions.FirstOrDefault(p => p.OffsetBytes == j.NewOffset);
            return moved is { Volume.FileSystem: "NTFS" } && moved.SizeBytes == j.SizeBytes
                ? new(OperationStatus.Succeeded, $"Moved {moved.DisplayName} to {ByteSize.Format(j.NewOffset)}. Its files were not touched; check it in Explorer.", steps, after)
                : new(OperationStatus.PartiallyApplied, "The move finished, but Windows does not show the volume as expected yet. Click Refresh, or check it in Disk Management.", steps, after);
        }
        finally
        {
            host.Release();
        }
    }
}
