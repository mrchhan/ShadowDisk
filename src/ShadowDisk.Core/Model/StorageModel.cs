namespace ShadowDisk.Core.Model;

// Physical disks, partitions, volumes and unallocated regions are modelled as separate
// concepts on purpose. A drive letter belongs to a volume; it is not a partition, and a
// partition is not a disk.

public enum PartitionStyle { Unknown, Raw, Mbr, Gpt }

public enum BusType { Unknown, Sata, Ata, Nvme, Usb, Sas, Scsi, Raid, SdCard, Mmc, Virtual, FileBackedVirtual, Other }

/// <summary>Health as reported by Windows. This is NOT a surface scan and does not prove a disk is safe.</summary>
public enum ReportedHealth { Unknown, Healthy, Warning, Unhealthy }

public enum PartitionKind
{
    Unknown,
    Basic,             // Ordinary data partition
    EfiSystem,         // EFI System Partition (GPT)
    MicrosoftReserved, // MSR (GPT)
    Recovery,          // Windows Recovery Environment / OEM recovery
    LdmMetadata,       // Dynamic disk metadata (GPT)
    LdmData,           // Dynamic disk data (GPT)
    DynamicMbr,        // MBR type 0x42 (dynamic disk)
    Extended,          // MBR extended container
    Logical,           // MBR logical partition inside an extended container
    OemOrHidden,       // Vendor / hidden partition we do not recognise
}

public enum EncryptionState { Unknown, NotEncrypted, Encrypted, Locked }

public sealed record Volume(
    string? DriveLetter,       // "C" — may be null; many volumes have no letter
    string? Label,
    string? FileSystem,        // "NTFS", "ReFS", "FAT32", "exFAT", "RAW", or null if unknown
    long SizeBytes,
    long FreeBytes,
    ReportedHealth Health,
    EncryptionState Encryption)
{
    public long UsedBytes => Math.Max(0, SizeBytes - FreeBytes);
}

public sealed record Partition(
    int DiskNumber,
    int PartitionNumber,
    long OffsetBytes,
    long SizeBytes,
    PartitionKind Kind,
    string? GptType,
    int? MbrType,
    bool IsBoot,
    bool IsSystem,
    bool IsActive,
    bool IsHidden,
    Volume? Volume)
{
    public long EndBytes => OffsetBytes + SizeBytes;

    /// <summary>
    /// Holds the files the PC needs to start: the EFI System Partition on GPT, or the
    /// active "System Reserved" partition on MBR (Windows reports IsSystem for both).
    /// </summary>
    public bool IsStartupPartition => IsSystem || Kind == PartitionKind.EfiSystem;

    public string DisplayName => Volume?.DriveLetter is { Length: > 0 } letter
        ? $"{letter}:"
        : Kind switch
        {
            PartitionKind.EfiSystem => "EFI System",
            PartitionKind.MicrosoftReserved => "Microsoft Reserved",
            PartitionKind.Recovery => "Recovery",
            PartitionKind.Extended => "Extended",
            PartitionKind.LdmMetadata or PartitionKind.LdmData or PartitionKind.DynamicMbr => "Dynamic",
            _ when Volume?.Label is { Length: > 0 } label => label,
            _ when IsSystem => "System partition",
            _ => $"Partition {PartitionNumber}",
        };
}

public sealed record PhysicalDisk(
    int Number,
    string FriendlyName,
    string? SerialNumber,
    long SizeBytes,
    int LogicalSectorSize,
    int PhysicalSectorSize,
    PartitionStyle Style,
    BusType Bus,
    bool IsBoot,
    bool IsSystem,
    bool IsOffline,
    bool IsReadOnly,
    ReportedHealth Health,
    IReadOnlyList<Partition> Partitions,
    string? Location = null,   // For a virtual disk: the path of its file. Used to recognise practice disks.
    string? UniqueId = null)   // Windows' identifier for the disk; used to make sure an operation targets the same disk.
{
    public bool IsRemovableBus => Bus is BusType.Usb or BusType.SdCard or BusType.Mmc;

    public bool IsDynamic => Partitions.Any(p =>
        p.Kind is PartitionKind.LdmMetadata or PartitionKind.LdmData or PartitionKind.DynamicMbr);
}

public sealed record InventorySnapshot(
    DateTimeOffset TakenAt,
    bool IsSimulated,
    string SourceDescription,
    IReadOnlyList<PhysicalDisk> Disks,
    IReadOnlyList<string> Warnings);
