using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Inventory;

/// <summary>
/// Read-only source of disk information. There is intentionally no write method on this
/// interface: operations that change a disk will live behind a separate, narrow interface
/// that does not exist yet.
/// </summary>
public interface IStorageInventory
{
    bool IsSimulated { get; }
    string SourceDescription { get; }
    Task<InventorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
