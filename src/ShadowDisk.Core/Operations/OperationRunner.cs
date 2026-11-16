using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Logging;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.Core.Operations;

/// <summary>
/// The only path from a confirmed plan to a real disk change.
///
/// 1. Read the disk again and refuse if it is not the same disk, or its layout changed.
/// 2. Check the policy (practice disks only) and validate the plan again on the fresh layout.
/// 3. Make ONE attempt. No retries. Cancellation is honoured only before writing starts.
/// 4. Read the disk back and report what actually happened.
/// </summary>
public sealed class OperationRunner(
    IStorageInventory inventory,
    IDiskOperations operations,
    OperationPolicy policy,
    OperationLog log,
    IPartitionLimitsProvider? limitsProvider = null,
    Func<bool?>? onMainsPower = null)
{
    private readonly IPartitionLimitsProvider _limits = limitsProvider ?? new EstimatedLimitsProvider();

    public async Task<OperationOutcome> CreateVolumeAsync(CreateVolumeOrder order, CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        void Step(string s) { steps.Add(s); log.Info("Create volume: " + s); }

        OperationOutcome Stop(OperationStatus status, string message, InventorySnapshot? after = null)
        {
            if (status == OperationStatus.Blocked || status == OperationStatus.Cancelled) log.Warning($"Create volume {status}: {message}");
            else if (status == OperationStatus.Succeeded) log.Info($"Create volume succeeded: {message}");
            else log.Error($"Create volume {status}: {message}");
            return new OperationOutcome(status, message, steps, after);
        }

        // 1. Fresh read
        InventorySnapshot before;
        try
        {
            before = await inventory.GetSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed.");
        }
        catch (Exception ex)
        {
            return Stop(OperationStatus.Blocked, $"Could not re-read the disk before starting ({ex.Message}). Nothing was changed.");
        }
        Step($"Re-read the disks ({before.Disks.Count} found).");

        var disk = before.Disks.FirstOrDefault(d => d.Number == order.DiskNumber);
        if (disk is null || !string.Equals(disk.UniqueId, order.DiskUniqueId, StringComparison.Ordinal))
            return Stop(OperationStatus.Blocked, $"Disk {order.DiskNumber} is no longer the disk the plan was made for. Nothing was changed. Refresh and plan again.");
        if (LayoutFingerprint.Of(disk) != order.LayoutFingerprint)
            return Stop(OperationStatus.Blocked, "The disk's layout changed since the plan was made. Nothing was changed. Refresh and plan again.");
        Step("Confirmed it is the same disk with the same layout.");

        // 2. Policy and validation on the fresh layout
        var notAllowed = policy.WhyNotAllowed(disk, null, onMainsPower?.Invoke());
        if (notAllowed.Count > 0)
            return Stop(OperationStatus.Blocked, string.Join(" ", notAllowed) + " Nothing was changed.");
        Step(policy.IsPracticeDisk(disk)
            ? "Confirmed this is a ShadowDisk practice disk."
            : "Real disk: confirmed real-disk changes are switched on and the safety checks pass.");

        var layout = DiskLayoutBuilder.Build(disk);
        var region = layout.Regions.FirstOrDefault(r => r.Kind == RegionKind.Unallocated &&
                                                        r.OffsetBytes <= order.OffsetBytes &&
                                                        r.EndBytes >= order.OffsetBytes + order.SizeBytes);
        if (region is null)
            return Stop(OperationStatus.Blocked, "The planned space is no longer unallocated. Nothing was changed.");
        var (offset, limits) = PlanLimits.ForNewVolume(layout, region);
        var validation = PlanValidator.Validate(layout, new PlanRequest(PlanKind.CreateVolume, region, order.SizeBytes), limits);
        if (!validation.IsValid || offset != order.OffsetBytes)
            return Stop(OperationStatus.Blocked, "The plan no longer passes its checks on the current disk. Nothing was changed.");
        Step("The plan passed all checks again.");

        if (cancellationToken.IsCancellationRequested)
            return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed.");

        // 3. One attempt. From here on the operation is not cancellable: stopping half-way is worse.
        Step($"Creating a {ByteSize.Format(order.SizeBytes)} NTFS volume at {ByteSize.Format(order.OffsetBytes)}…");
        CreateVolumeBackendResult result;
        try
        {
            result = await operations.CreateVolumeAsync(disk, order.OffsetBytes, order.SizeBytes, order.VolumeLabel);
        }
        catch (Exception ex)
        {
            result = new CreateVolumeBackendResult(false, false, null, ex.Message);
        }
        if (result.PartitionCreated) Step("Windows created the partition.");
        if (result.Formatted) Step("Windows formatted it as NTFS.");
        if (result.DriveLetter is { } letter) Step($"Windows assigned drive letter {letter}:.");
        if (result.Error is { } error) Step($"Windows reported a problem: {error}");

        // 4. Read back
        InventorySnapshot? after = null;
        try
        {
            after = await inventory.GetSnapshotAsync(CancellationToken.None);
            Step("Re-read the disk to check the result.");
        }
        catch (Exception ex)
        {
            Step($"Could not re-read the disk afterwards ({ex.Message}). Check it in Windows Disk Management.");
        }

        var created = after?.Disks.FirstOrDefault(d => d.Number == order.DiskNumber)?
            .Partitions.FirstOrDefault(p => p.OffsetBytes == order.OffsetBytes);

        if (result.Completed && created is not null && created.SizeBytes == order.SizeBytes && created.Volume?.FileSystem == "NTFS")
            return Stop(OperationStatus.Succeeded,
                $"Created a {ByteSize.Format(created.SizeBytes)} NTFS volume{(created.Volume.DriveLetter is { } l ? $" ({l}:)" : "")}.", after);

        if (result.PartitionCreated || created is not null)
            return Stop(OperationStatus.PartiallyApplied,
                "The partition was created, but not every step finished. The disk now shows what Windows actually did; " +
                "ShadowDisk did not try to undo anything. Check the disk in Windows Disk Management before doing anything else.", after);

        return Stop(OperationStatus.Failed, $"Windows did not create the volume. {result.Error ?? "No reason was given."} The read-back shows the disk's current state.", after);
    }

    /// <summary>
    /// Shrinks or extends one partition. Same safety steps as creating a volume, plus the size limits
    /// are asked for AGAIN just before writing, so a shrink never goes below what Windows allows now.
    /// </summary>
    public async Task<OperationOutcome> ResizeAsync(ResizeOrder order, CancellationToken cancellationToken)
    {
        var verb = order.IsShrink ? "Shrink" : "Extend";
        var steps = new List<string>();
        void Step(string s) { steps.Add(s); log.Info($"{verb}: {s}"); }

        OperationOutcome Stop(OperationStatus status, string message, InventorySnapshot? after = null)
        {
            if (status is OperationStatus.Blocked or OperationStatus.Cancelled) log.Warning($"{verb} {status}: {message}");
            else if (status == OperationStatus.Succeeded) log.Info($"{verb} succeeded: {message}");
            else log.Error($"{verb} {status}: {message}");
            return new OperationOutcome(status, message, steps, after);
        }

        if (order.TargetSizeBytes == order.CurrentSizeBytes)
            return Stop(OperationStatus.Blocked, "The new size is the same as the current size. Nothing was changed.");

        // 1. Fresh read and identity
        InventorySnapshot before;
        try
        {
            before = await inventory.GetSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed.");
        }
        catch (Exception ex)
        {
            return Stop(OperationStatus.Blocked, $"Could not re-read the disk before starting ({ex.Message}). Nothing was changed.");
        }
        Step($"Re-read the disks ({before.Disks.Count} found).");

        var disk = before.Disks.FirstOrDefault(d => d.Number == order.DiskNumber);
        if (disk is null || !string.Equals(disk.UniqueId, order.DiskUniqueId, StringComparison.Ordinal))
            return Stop(OperationStatus.Blocked, $"Disk {order.DiskNumber} is no longer the disk the plan was made for. Nothing was changed. Refresh and plan again.");
        if (LayoutFingerprint.Of(disk) != order.LayoutFingerprint)
            return Stop(OperationStatus.Blocked, "The disk's layout changed since the plan was made. Nothing was changed. Refresh and plan again.");
        var partition = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == order.PartitionNumber && p.OffsetBytes == order.OffsetBytes);
        if (partition is null || partition.SizeBytes != order.CurrentSizeBytes)
            return Stop(OperationStatus.Blocked, "The partition is not where or what the plan expected. Nothing was changed.");
        Step("Confirmed it is the same disk, layout and partition.");

        // 2. Policy, fresh limits, validation
        var notAllowed = policy.WhyNotAllowed(disk, partition, onMainsPower?.Invoke());
        if (notAllowed.Count > 0)
            return Stop(OperationStatus.Blocked, string.Join(" ", notAllowed) + " Nothing was changed.");
        Step(policy.IsPracticeDisk(disk)
            ? "Confirmed this is a ShadowDisk practice disk."
            : $"Real disk: confirmed real-disk changes are switched on and {partition.DisplayName} passes the safety checks.");

        var layout = DiskLayoutBuilder.Build(disk);
        var region = layout.Regions.First(r => ReferenceEquals(r.Partition, partition));
        var kind = order.IsShrink ? PlanKind.ShrinkVolume : PlanKind.ExtendVolume;
        SizeLimits limits;
        try
        {
            limits = await _limits.GetLimitsAsync(layout, partition, kind, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed.");
        }
        Step($"Asked for the size limits again: {ByteSize.Format(limits.MinBytes)} – {ByteSize.Format(limits.MaxBytes)} ({limits.Source}).");

        var validation = PlanValidator.Validate(layout, new PlanRequest(kind, region, order.TargetSizeBytes), limits);
        if (!validation.IsValid)
        {
            var failed = validation.Checks.FirstOrDefault(c => c.Outcome == CheckOutcome.Failed)?.Text ?? "";
            return Stop(OperationStatus.Blocked, $"The plan no longer passes its checks on the current disk. {failed} Nothing was changed.");
        }
        Step("The plan passed all checks again.");

        if (cancellationToken.IsCancellationRequested)
            return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed.");

        // 3. One attempt
        Step($"{(order.IsShrink ? "Shrinking" : "Extending")} {partition.DisplayName} from {ByteSize.Format(order.CurrentSizeBytes)} to {ByteSize.Format(order.TargetSizeBytes)}…");
        ResizeBackendResult result;
        try
        {
            result = await operations.ResizePartitionAsync(disk, order.PartitionNumber, order.OffsetBytes, order.TargetSizeBytes);
        }
        catch (Exception ex)
        {
            result = new ResizeBackendResult(false, ex.Message);
        }
        if (result.Resized) Step("Windows reported the resize as finished.");
        if (result.Error is { } error) Step($"Windows reported a problem: {error}");

        // 4. Read back
        InventorySnapshot? after = null;
        try
        {
            after = await inventory.GetSnapshotAsync(CancellationToken.None);
            Step("Re-read the disk to check the result.");
        }
        catch (Exception ex)
        {
            Step($"Could not re-read the disk afterwards ({ex.Message}). Check it in Windows Disk Management.");
        }

        var now = after?.Disks.FirstOrDefault(d => d.Number == order.DiskNumber)?
            .Partitions.FirstOrDefault(p => p.OffsetBytes == order.OffsetBytes);

        if (now is not null && now.SizeBytes == order.TargetSizeBytes && result.Error is null)
            return Stop(OperationStatus.Succeeded, $"{partition.DisplayName} is now {ByteSize.Format(now.SizeBytes)}.", after);

        if (now is not null && now.SizeBytes != order.CurrentSizeBytes)
            return Stop(OperationStatus.PartiallyApplied,
                $"{partition.DisplayName} changed size, but to {ByteSize.Format(now.SizeBytes)} instead of {ByteSize.Format(order.TargetSizeBytes)}. " +
                "ShadowDisk did not try to correct it. The disk now shows its real state.", after);

        if (now is null && after is not null)
            return Stop(OperationStatus.PartiallyApplied,
                "After the change, the partition could not be found where it was. Check the disk in Windows Disk Management before doing anything else.", after);

        return Stop(OperationStatus.Failed,
            $"Windows did not resize {partition.DisplayName}. {result.Error ?? "No reason was given."} The read-back shows the disk's current state.", after);
    }

    /// <summary>Partition kinds that may be deleted: ordinary data partitions only.</summary>
    public static string? WhyKindNotDeletable(Partition p) =>
        p.IsBoot || p.IsSystem || p.IsStartupPartition
            ? $"{p.DisplayName} holds Windows or the files your PC needs to start. It can never be deleted by ShadowDisk."
            : p.Kind is PartitionKind.Basic or PartitionKind.Logical
                ? null
                : $"{p.DisplayName} is a {p.Kind} partition. ShadowDisk only deletes ordinary data volumes.";

    /// <summary>
    /// Deletes one data partition. Besides the usual identity, layout and policy checks, <paramref name="whyNotSafe"/>
    /// must confirm the volume's files are safe to lose (empty, or verified-copied and unchanged).
    /// </summary>
    public async Task<OperationOutcome> DeleteAsync(DeleteOrder order, Func<Partition, string?> whyNotSafe, CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        void Step(string s) { steps.Add(s); log.Info($"Delete: {s}"); }
        OperationOutcome Stop(OperationStatus status, string message, InventorySnapshot? after = null)
        {
            if (status is OperationStatus.Blocked or OperationStatus.Cancelled) log.Warning($"Delete {status}: {message}");
            else if (status == OperationStatus.Succeeded) log.Info($"Delete succeeded: {message}");
            else log.Error($"Delete {status}: {message}");
            return new OperationOutcome(status, message, steps, after);
        }

        InventorySnapshot before;
        try { before = await inventory.GetSnapshotAsync(cancellationToken); }
        catch (OperationCanceledException) { return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed."); }
        catch (Exception ex) { return Stop(OperationStatus.Blocked, $"Could not re-read the disk before starting ({ex.Message}). Nothing was changed."); }
        Step($"Re-read the disks ({before.Disks.Count} found).");

        var disk = before.Disks.FirstOrDefault(d => d.Number == order.DiskNumber);
        if (disk is null || !string.Equals(disk.UniqueId, order.DiskUniqueId, StringComparison.Ordinal))
            return Stop(OperationStatus.Blocked, $"Disk {order.DiskNumber} is no longer the disk that was chosen. Nothing was changed.");
        if (LayoutFingerprint.Of(disk) != order.LayoutFingerprint)
            return Stop(OperationStatus.Blocked, "The disk's layout changed since you chose this. Nothing was changed. Refresh and try again.");
        var partition = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == order.PartitionNumber && p.OffsetBytes == order.OffsetBytes);
        if (partition is null || partition.SizeBytes != order.SizeBytes)
            return Stop(OperationStatus.Blocked, "The partition is not where or what was expected. Nothing was changed.");
        Step($"Confirmed it is the same disk, layout and partition ({partition.DisplayName}).");

        if (WhyKindNotDeletable(partition) is { } kindReason)
            return Stop(OperationStatus.Blocked, kindReason + " Nothing was changed.");
        var notAllowed = policy.WhyNotAllowed(disk, partition, onMainsPower?.Invoke());
        if (notAllowed.Count > 0)
            return Stop(OperationStatus.Blocked, string.Join(" ", notAllowed) + " Nothing was changed.");
        Step("The disk and partition pass the safety checks.");

        string? unsafeReason;
        try { unsafeReason = whyNotSafe(partition); }
        catch (Exception ex) { unsafeReason = $"ShadowDisk could not check the volume's files ({ex.Message})."; }
        if (unsafeReason is not null)
            return Stop(OperationStatus.Blocked, unsafeReason + " Nothing was changed.");
        Step("Confirmed the volume is empty, or its files have a verified copy and have not changed since.");

        if (cancellationToken.IsCancellationRequested)
            return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed.");

        Step($"Deleting {partition.DisplayName} ({ByteSize.Format(partition.SizeBytes)})…");
        BackendResult result;
        try { result = await operations.DeletePartitionAsync(disk, order.PartitionNumber, order.OffsetBytes); }
        catch (Exception ex) { result = new BackendResult(false, ex.Message); }
        if (result.Done) Step("Windows reported the partition as deleted.");
        if (result.Error is { } error) Step($"Windows reported a problem: {error}");

        InventorySnapshot? after = null;
        try { after = await inventory.GetSnapshotAsync(CancellationToken.None); Step("Re-read the disk to check the result."); }
        catch (Exception ex) { Step($"Could not re-read the disk afterwards ({ex.Message}). Check it in Windows Disk Management."); }

        var stillThere = after?.Disks.FirstOrDefault(d => d.Number == order.DiskNumber)?.Partitions.Any(p => p.OffsetBytes == order.OffsetBytes);
        if (stillThere == false)
            return Stop(OperationStatus.Succeeded, $"{partition.DisplayName} was deleted. Its {ByteSize.Format(partition.SizeBytes)} is now unallocated space.", after);
        if (stillThere == true)
            return Stop(OperationStatus.Failed, $"Windows did not delete {partition.DisplayName}. {result.Error ?? "No reason was given."} The volume is still there.", after);
        return Stop(OperationStatus.PartiallyApplied, "The result could not be confirmed. Check the disk in Windows Disk Management before doing anything else.", after);
    }

    /// <summary>Changes (or assigns) a data volume's drive letter.</summary>
    public async Task<OperationOutcome> ChangeDriveLetterAsync(DriveLetterOrder order, CancellationToken cancellationToken)
    {
        var steps = new List<string>();
        void Step(string s) { steps.Add(s); log.Info($"Drive letter: {s}"); }
        OperationOutcome Stop(OperationStatus status, string message, InventorySnapshot? after = null)
        {
            if (status is OperationStatus.Blocked or OperationStatus.Cancelled) log.Warning($"Drive letter {status}: {message}");
            else if (status == OperationStatus.Succeeded) log.Info($"Drive letter succeeded: {message}");
            else log.Error($"Drive letter {status}: {message}");
            return new OperationOutcome(status, message, steps, after);
        }

        var letter = order.NewLetter.Trim().TrimEnd(':').ToUpperInvariant();
        if (letter.Length != 1 || letter[0] < 'D' || letter[0] > 'Z')
            return Stop(OperationStatus.Blocked, "Choose a drive letter from D to Z. Nothing was changed.");

        InventorySnapshot before;
        try { before = await inventory.GetSnapshotAsync(cancellationToken); }
        catch (OperationCanceledException) { return Stop(OperationStatus.Cancelled, "Cancelled before anything was changed."); }
        catch (Exception ex) { return Stop(OperationStatus.Blocked, $"Could not re-read the disks ({ex.Message}). Nothing was changed."); }

        var disk = before.Disks.FirstOrDefault(d => d.Number == order.DiskNumber);
        if (disk is null || !string.Equals(disk.UniqueId, order.DiskUniqueId, StringComparison.Ordinal))
            return Stop(OperationStatus.Blocked, "The disk is no longer the one that was chosen. Nothing was changed.");
        var partition = disk.Partitions.FirstOrDefault(p => p.PartitionNumber == order.PartitionNumber && p.OffsetBytes == order.OffsetBytes);
        if (partition?.Volume is not { } volume)
            return Stop(OperationStatus.Blocked, "The volume is not where it was expected. Nothing was changed.");
        if (!string.Equals(volume.DriveLetter, order.OldLetter, StringComparison.OrdinalIgnoreCase))
            return Stop(OperationStatus.Blocked, "The volume's drive letter changed in the meantime. Nothing was changed. Refresh and try again.");
        if (partition.IsBoot || partition.IsSystem || partition.IsStartupPartition)
            return Stop(OperationStatus.Blocked, $"{partition.DisplayName} holds Windows or its start-up files; its letter must not change. Nothing was changed.");
        var notAllowed = policy.WhyNotAllowed(disk, partition, null);
        if (notAllowed.Count > 0)
            return Stop(OperationStatus.Blocked, string.Join(" ", notAllowed) + " Nothing was changed.");
        if (before.Disks.SelectMany(d => d.Partitions).Any(p => string.Equals(p.Volume?.DriveLetter, letter, StringComparison.OrdinalIgnoreCase)))
            return Stop(OperationStatus.Blocked, $"{letter}: is already used by another volume. Nothing was changed.");
        Step($"Checks passed: {(order.OldLetter is { } o ? o + ":" : "the volume")} → {letter}:.");

        BackendResult result;
        try { result = await operations.SetDriveLetterAsync(disk, order.PartitionNumber, order.OffsetBytes, order.OldLetter, letter); }
        catch (Exception ex) { result = new BackendResult(false, ex.Message); }
        if (result.Error is { } error) Step($"Windows reported a problem: {error}");

        InventorySnapshot? after = null;
        try { after = await inventory.GetSnapshotAsync(CancellationToken.None); Step("Re-read the disks to check the result."); }
        catch (Exception ex) { Step($"Could not re-read the disks afterwards ({ex.Message})."); }
        var now = after?.Disks.FirstOrDefault(d => d.Number == order.DiskNumber)?.Partitions
            .FirstOrDefault(p => p.OffsetBytes == order.OffsetBytes)?.Volume?.DriveLetter;

        if (string.Equals(now, letter, StringComparison.OrdinalIgnoreCase))
            return Stop(OperationStatus.Succeeded, $"The volume is now {letter}:.", after);
        if (now is null && order.OldLetter is not null)
            return Stop(OperationStatus.PartiallyApplied,
                $"The old letter was removed but {letter}: was not assigned. The volume has no letter now; give it one with “Change drive letter”. Its files are untouched.", after);
        return Stop(OperationStatus.Failed, $"Windows did not change the letter. {result.Error ?? "No reason was given."}", after);
    }
}
