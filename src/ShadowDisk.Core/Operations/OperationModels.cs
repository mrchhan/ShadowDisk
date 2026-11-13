using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Operations;

// Stage 3: real operations. Everything that can change a disk goes through OperationRunner,
// which re-checks the disk immediately before writing and reads it back afterwards.

/// <summary>
/// An instruction to create a new volume, captured at the moment the user confirmed.
/// <see cref="LayoutFingerprint"/> is the layout the plan was made against: if the disk no longer
/// matches it, the operation is refused.
/// </summary>
public sealed record CreateVolumeOrder(
    int DiskNumber,
    string? DiskUniqueId,
    string LayoutFingerprint,
    long OffsetBytes,
    long SizeBytes,
    string VolumeLabel);

/// <summary>An instruction to shrink or extend one partition (and its volume) to an exact size.</summary>
public sealed record ResizeOrder(
    int DiskNumber,
    string? DiskUniqueId,
    string LayoutFingerprint,
    int PartitionNumber,
    long OffsetBytes,
    long CurrentSizeBytes,
    long TargetSizeBytes)
{
    public bool IsShrink => TargetSizeBytes < CurrentSizeBytes;
}

public enum OperationStatus
{
    Succeeded,          // Every step finished and the read-back matches
    PartiallyApplied,   // Some steps changed the disk, then a step failed — the disk is in between
    Failed,             // The change was attempted but did not happen
    Blocked,            // Refused before anything was written
    Cancelled,          // Cancelled before anything was written
}

public sealed record OperationOutcome(
    OperationStatus Status,
    string Message,
    IReadOnlyList<string> Steps,
    InventorySnapshot? StateAfter)
{
    /// <summary>True when the disk may have been changed (successfully or not).</summary>
    public bool DiskMayHaveChanged => Status is OperationStatus.Succeeded or OperationStatus.PartiallyApplied or OperationStatus.Failed;
}

/// <summary>What the backend actually managed to do. Each step runs once; nothing is retried.</summary>
public sealed record CreateVolumeBackendResult(
    bool PartitionCreated,
    bool Formatted,
    string? DriveLetter,
    string? Error)
{
    public bool Completed => PartitionCreated && Formatted && Error is null;
}

/// <summary>What the backend reported for a resize. One call; nothing is retried.</summary>
public sealed record ResizeBackendResult(bool Resized, string? Error);

/// <summary>
/// The narrow interface to code that writes to disks. Implementations must do exactly what is
/// asked — one attempt, no retries, no extra steps — and must refuse any disk that is not a
/// ShadowDisk practice disk.
/// </summary>
public interface IDiskOperations
{
    Task<CreateVolumeBackendResult> CreateVolumeAsync(PhysicalDisk disk, long offsetBytes, long sizeBytes, string volumeLabel);

    /// <summary>Resizes one partition and its file system (shrink or extend) to exactly <paramref name="targetSizeBytes"/>.</summary>
    Task<ResizeBackendResult> ResizePartitionAsync(PhysicalDisk disk, int partitionNumber, long offsetBytes, long targetSizeBytes);
}
