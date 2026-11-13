using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Planning;

// Everything in this namespace is SIMULATION. A plan describes a change; it never performs one.

public enum PlanKind { ShrinkVolume, ExtendVolume, CreateVolume }

public enum LimitsSource
{
    Windows,      // Reported by Windows (MSFT_Partition.GetSupportedSize) — accounts for unmovable files
    Estimated,    // Worked out by ShadowDisk from used space; Windows' real limit may differ
    FreeSpace,    // For a new volume: simply the size of the unallocated region
}

/// <summary>Smallest and largest allowed target size for a plan, in bytes.</summary>
public sealed record SizeLimits(long MinBytes, long MaxBytes, LimitsSource Source, string? Note = null)
{
    public bool IsUsable => MinBytes > 0 && MaxBytes >= MinBytes;
}

/// <summary>
/// What the user wants to try. For shrink/extend, <see cref="Partition"/> is set; for a new volume,
/// <see cref="Region"/> is the unallocated region.
/// </summary>
public sealed record PlanRequest(PlanKind Kind, DiskRegion Region, long TargetSizeBytes)
{
    public Partition? Partition => Region.Partition;
}

public enum CheckOutcome { Passed, Warning, Failed }

public sealed record PlanCheck(CheckOutcome Outcome, string Text);

public sealed record PlanResult(
    PlanRequest Request,
    SizeLimits Limits,
    IReadOnlyList<PlanCheck> Checks,
    DiskLayout? ProposedLayout,
    DiskRegion? ProposedRegion,
    string Summary,
    IReadOnlyList<string> Risks)
{
    public bool IsValid => Checks.All(c => c.Outcome != CheckOutcome.Failed);
}

/// <summary>Source of size limits. The Windows implementation only reads; it never resizes anything.</summary>
public interface IPartitionLimitsProvider
{
    Task<SizeLimits> GetLimitsAsync(DiskLayout layout, Partition partition, PlanKind kind, CancellationToken cancellationToken);
}
