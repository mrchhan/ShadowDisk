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
public sealed class OperationRunner(IStorageInventory inventory, IDiskOperations operations, OperationPolicy policy, OperationLog log)
{
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
        var notAllowed = policy.WhyNotAllowed(disk);
        if (notAllowed.Count > 0)
            return Stop(OperationStatus.Blocked, string.Join(" ", notAllowed) + " Nothing was changed.");
        Step("Confirmed this is a ShadowDisk practice disk.");

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
                "ShadowDisk did not try to undo anything. On a practice disk you can simply remove it and start again.", after);

        return Stop(OperationStatus.Failed, $"Windows did not create the volume. {result.Error ?? "No reason was given."} The read-back shows the disk's current state.", after);
    }
}
