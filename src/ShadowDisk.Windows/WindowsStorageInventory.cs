using System.Management;
using System.Runtime.Versioning;
using ShadowDisk.Core.Inventory;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Windows;

/// <summary>
/// Reads the real disk layout through the documented Windows Storage Management API
/// (WMI namespace root\Microsoft\Windows\Storage — the same API behind Get-Disk / Get-Partition /
/// Get-Volume). Only SELECT queries and the read-only GetLockStatus method are used.
/// This class never calls any method that changes a disk.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStorageInventory : IStorageInventory
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";
    private const string BitLockerNamespace = @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption";

    public bool IsSimulated => false;
    public string SourceDescription => "This PC (live, read-only)";

    public Task<InventorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    private static InventorySnapshot Read(CancellationToken ct)
    {
        var warnings = new List<string>();
        var scope = new ManagementScope(StorageNamespace);
        scope.Connect();

        var disks = Query(scope,
            "SELECT Number, FriendlyName, SerialNumber, Size, LogicalSectorSize, PhysicalSectorSize, PartitionStyle, " +
            "BusType, IsBoot, IsSystem, IsOffline, IsReadOnly, HealthStatus FROM MSFT_Disk",
            o => new RawDisk(
                Get<uint>(o, "Number"),
                Get<string>(o, "FriendlyName"),
                Get<string>(o, "SerialNumber"),
                Get<ulong>(o, "Size"),
                Get<uint>(o, "LogicalSectorSize"),
                Get<uint>(o, "PhysicalSectorSize"),
                Get<ushort>(o, "PartitionStyle"),
                Get<ushort>(o, "BusType"),
                Get<bool>(o, "IsBoot"),
                Get<bool>(o, "IsSystem"),
                Get<bool>(o, "IsOffline"),
                Get<bool>(o, "IsReadOnly"),
                Get<ushort>(o, "HealthStatus", 5)), ct);

        var partitions = Query(scope,
            "SELECT DiskNumber, PartitionNumber, Offset, Size, GptType, MbrType, IsBoot, IsSystem, IsActive, IsHidden, AccessPaths FROM MSFT_Partition",
            o => new RawPartition(
                Get<uint>(o, "DiskNumber"),
                Get<uint>(o, "PartitionNumber"),
                Get<ulong>(o, "Offset"),
                Get<ulong>(o, "Size"),
                Get<string>(o, "GptType"),
                o["MbrType"] is null ? null : Get<ushort>(o, "MbrType"),
                Get<bool>(o, "IsBoot"),
                Get<bool>(o, "IsSystem"),
                Get<bool>(o, "IsActive"),
                Get<bool>(o, "IsHidden"),
                (o["AccessPaths"] as string[]) ?? []), ct);

        var volumes = Query(scope,
            "SELECT Path, DriveLetter, FileSystem, FileSystemLabel, Size, SizeRemaining, HealthStatus FROM MSFT_Volume",
            o => new RawVolume(
                Get<string>(o, "Path") ?? "",
                o["DriveLetter"] is char c && c != '\0' ? c : null,
                Get<string>(o, "FileSystem"),
                Get<string>(o, "FileSystemLabel"),
                Get<ulong>(o, "Size"),
                Get<ulong>(o, "SizeRemaining"),
                Get<ushort>(o, "HealthStatus", 5)), ct);

        var encryption = new List<RawEncryption>();
        var encryptionKnown = false;
        try
        {
            encryption = ReadBitLocker(ct);
            encryptionKnown = true;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            warnings.Add("BitLocker status needs administrator rights. Use “Restart as administrator” to see it.");
        }
        catch (UnauthorizedAccessException)
        {
            warnings.Add("BitLocker status needs administrator rights. Use “Restart as administrator” to see it.");
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass)
        {
            // Some Windows editions (e.g. Home) do not expose the BitLocker WMI provider.
            warnings.Add("BitLocker information is not available on this edition of Windows.");
        }

        ct.ThrowIfCancellationRequested();
        var mapped = StorageMapper.Map(disks, partitions, volumes, encryption, encryptionKnown);
        return new InventorySnapshot(DateTimeOffset.Now, false, "This PC (live, read-only)", mapped, warnings);
    }

    private static List<RawEncryption> ReadBitLocker(CancellationToken ct)
    {
        var scope = new ManagementScope(BitLockerNamespace);
        scope.Connect();
        var list = new List<RawEncryption>();
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT DeviceID, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume"));
        using var results = searcher.Get();
        foreach (ManagementObject o in results)
        {
            using (o)
            {
                ct.ThrowIfCancellationRequested();
                bool? locked = null;
                try
                {
                    // GetLockStatus only reads state. 0 = unlocked, 1 = locked.
                    using var outParams = o.InvokeMethod("GetLockStatus", null, null);
                    if (outParams?["LockStatus"] is { } ls) locked = Convert.ToUInt32(ls) == 1;
                }
                catch (ManagementException) { /* leave unknown */ }

                list.Add(new RawEncryption(
                    Get<string>(o, "DeviceID") ?? "",
                    Get<uint>(o, "ProtectionStatus", 2),
                    locked,
                    o["ConversionStatus"] is null ? null : Get<uint>(o, "ConversionStatus")));
            }
        }
        return list;
    }

    private static List<T> Query<T>(ManagementScope scope, string wql, Func<ManagementBaseObject, T> map, CancellationToken ct)
    {
        var list = new List<T>();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var o in results)
        {
            using (o)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(map(o));
            }
        }
        return list;
    }

    private static T Get<T>(ManagementBaseObject o, string name, T fallback = default!)
    {
        var value = o[name];
        if (value is null) return fallback;
        if (value is T typed) return typed;
        try
        {
            return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return fallback;
        }
    }
}
