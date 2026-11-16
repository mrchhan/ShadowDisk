using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Operations;

namespace ShadowDisk.Core.Recovery;

// Stage 5, part 4: Windows Recovery Environment (WinRE).
//
// The Recovery partition after C: holds WinRE (the blue "Choose an option" screens). Before it can be
// moved, WinRE is turned off with Microsoft's own tool (reagentc /disable), which moves the recovery
// image (Winre.wim) into C:\Windows\System32\Recovery. Only when that image is confirmed there may the
// old partition be deleted. Afterwards a new Recovery partition is made and WinRE is turned back on
// (reagentc /enable), which moves the image onto it. This is the method in Microsoft KB5028997.

/// <summary>What Windows reports about its recovery environment.</summary>
/// <param name="Enabled">True when WinRE is turned on.</param>
/// <param name="DiskNumber">Disk of the partition WinRE is on, when enabled.</param>
/// <param name="PartitionNumber">Partition WinRE is on, when enabled.</param>
/// <param name="ImageOnWindowsDrive">True when Winre.wim is in C:\Windows\System32\Recovery (where reagentc /disable puts it).</param>
/// <param name="Details">Plain text for the log and for the user.</param>
public sealed record RecoveryStatus(bool Enabled, int? DiskNumber, int? PartitionNumber, bool ImageOnWindowsDrive, string Details)
{
    public bool IsOn(int disk, int partition) => Enabled && DiskNumber == disk && PartitionNumber == partition;
}

public interface IRecoveryEnvironment
{
    /// <summary>True for the simulated environment used on practice disks.</summary>
    bool IsSimulated { get; }

    Task<RecoveryStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Turns WinRE off (reagentc /disable). One attempt.</summary>
    Task<BackendResult> DisableAsync(CancellationToken ct);

    /// <summary>Turns WinRE on (reagentc /enable). Windows picks the Recovery partition on the Windows disk. One attempt.</summary>
    Task<BackendResult> EnableAsync(CancellationToken ct);
}

/// <summary>
/// Stand-in for WinRE on a practice disk (and in tests). It behaves like reagentc: "disable" moves the
/// image to the Windows drive; "enable" moves it onto the first Recovery partition of the disk.
/// Until first used it reports WinRE as on, on the disk's first Recovery partition, like a normal PC.
/// </summary>
public sealed class SimulatedRecoveryEnvironment(IStorageInventory inventory, int diskNumber, string? diskUniqueId) : IRecoveryEnvironment
{
    private RecoveryStatus? _state;

    public bool IsSimulated => true;

    public async Task<RecoveryStatus> GetStatusAsync(CancellationToken ct)
    {
        if (_state is not null) return _state;
        var recovery = await FindRecoveryAsync(ct);
        return recovery is null
            ? new RecoveryStatus(false, null, null, false, "Practice disk: no Recovery partition, so the simulated Windows Recovery is off.")
            : new RecoveryStatus(true, diskNumber, recovery.PartitionNumber, false,
                $"Practice disk: simulated Windows Recovery is on partition {recovery.PartitionNumber}.");
    }

    public async Task<BackendResult> DisableAsync(CancellationToken ct)
    {
        var now = await GetStatusAsync(ct);
        if (!now.Enabled) return new BackendResult(true, null);   // reagentc reports success when already off
        _state = new RecoveryStatus(false, null, null, true, "Practice disk: simulated Windows Recovery is off; its image is on the Windows drive.");
        return new BackendResult(true, null);
    }

    public async Task<BackendResult> EnableAsync(CancellationToken ct)
    {
        var now = await GetStatusAsync(ct);
        if (now.Enabled) return new BackendResult(true, null);
        if (!now.ImageOnWindowsDrive) return new BackendResult(false, "The recovery image was not found.");
        var recovery = await FindRecoveryAsync(ct);
        if (recovery is null) return new BackendResult(false, "There is no Recovery partition to put the image on.");
        _state = new RecoveryStatus(true, diskNumber, recovery.PartitionNumber, false,
            $"Practice disk: simulated Windows Recovery is on partition {recovery.PartitionNumber}.");
        return new BackendResult(true, null);
    }

    private async Task<Partition?> FindRecoveryAsync(CancellationToken ct)
    {
        var snapshot = await inventory.GetSnapshotAsync(ct);
        var disk = snapshot.Disks.FirstOrDefault(d => d.Number == diskNumber && d.UniqueId == diskUniqueId);
        return disk?.Partitions.OrderBy(p => p.OffsetBytes).FirstOrDefault(p => p.Kind == PartitionKind.Recovery);
    }
}
