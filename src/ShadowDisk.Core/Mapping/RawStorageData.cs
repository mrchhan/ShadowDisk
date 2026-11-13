namespace ShadowDisk.Core.Mapping;

// Plain values exactly as Windows reports them (MSFT_Disk / MSFT_Partition / MSFT_Volume /
// Win32_EncryptableVolume). The Windows backend only fills these in; every decision about what
// the values mean lives in StorageMapper so it can be unit-tested without a real disk.

public sealed record RawDisk(
    uint Number,
    string? FriendlyName,
    string? SerialNumber,
    ulong Size,
    uint LogicalSectorSize,
    uint PhysicalSectorSize,
    ushort PartitionStyle,   // 0 Unknown/RAW, 1 MBR, 2 GPT
    ushort BusType,          // STORAGE_BUS_TYPE values
    bool IsBoot,
    bool IsSystem,
    bool IsOffline,
    bool IsReadOnly,
    ushort HealthStatus,     // 0 Healthy, 1 Warning, 2 Unhealthy, 5 Unknown
    string? Location = null,
    string? UniqueId = null);

public sealed record RawPartition(
    uint DiskNumber,
    uint PartitionNumber,
    ulong Offset,
    ulong Size,
    string? GptType,
    ushort? MbrType,
    bool IsBoot,
    bool IsSystem,
    bool IsActive,
    bool IsHidden,
    IReadOnlyList<string> AccessPaths);  // e.g. "C:\", "\\?\Volume{guid}\"

public sealed record RawVolume(
    string Path,              // "\\?\Volume{guid}\"
    char? DriveLetter,
    string? FileSystem,
    string? FileSystemLabel,
    ulong Size,
    ulong SizeRemaining,
    ushort HealthStatus);

/// <summary>ProtectionStatus: 0 off, 1 on, 2 unknown. Lock status: true when locked.</summary>
public sealed record RawEncryption(string VolumePath, uint ProtectionStatus, bool? IsLocked, uint? ConversionStatus);
