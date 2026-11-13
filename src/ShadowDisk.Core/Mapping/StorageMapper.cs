using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Mapping;

public static class StorageMapper
{
    // Well-known GPT partition type GUIDs (lower case, with braces).
    public const string GptEfiSystem = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    public const string GptMicrosoftReserved = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    public const string GptBasicData = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    public const string GptRecovery = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
    public const string GptLdmMetadata = "{5808c8aa-7e8f-42e0-85d2-e1e90434cfb3}";
    public const string GptLdmData = "{af9b60a0-1431-4f62-bc68-3311714a69ad}";

    public static PartitionStyle MapStyle(ushort value) => value switch
    {
        1 => PartitionStyle.Mbr,
        2 => PartitionStyle.Gpt,
        0 => PartitionStyle.Raw,
        _ => PartitionStyle.Unknown,
    };

    public static BusType MapBus(ushort value) => value switch
    {
        1 => BusType.Scsi,
        3 => BusType.Ata,
        7 => BusType.Usb,
        8 => BusType.Raid,
        10 => BusType.Sas,
        11 => BusType.Sata,
        12 => BusType.SdCard,
        13 => BusType.Mmc,
        14 => BusType.Virtual,
        15 => BusType.FileBackedVirtual,
        17 => BusType.Nvme,
        0 => BusType.Unknown,
        _ => BusType.Other,
    };

    public static ReportedHealth MapHealth(ushort value) => value switch
    {
        0 => ReportedHealth.Healthy,
        1 => ReportedHealth.Warning,
        2 => ReportedHealth.Unhealthy,
        _ => ReportedHealth.Unknown,
    };

    public static PartitionKind ClassifyGpt(string? gptType)
    {
        if (string.IsNullOrWhiteSpace(gptType)) return PartitionKind.Unknown;
        var g = NormalizeGuid(gptType);
        return g switch
        {
            GptEfiSystem => PartitionKind.EfiSystem,
            GptMicrosoftReserved => PartitionKind.MicrosoftReserved,
            GptBasicData => PartitionKind.Basic,
            GptRecovery => PartitionKind.Recovery,
            GptLdmMetadata => PartitionKind.LdmMetadata,
            GptLdmData => PartitionKind.LdmData,
            _ => PartitionKind.OemOrHidden,
        };
    }

    public static PartitionKind ClassifyMbr(int? mbrType) => mbrType switch
    {
        null => PartitionKind.Unknown,
        0x05 or 0x0F or 0x85 => PartitionKind.Extended,
        0x42 => PartitionKind.DynamicMbr,
        0x27 => PartitionKind.Recovery,
        0xEF => PartitionKind.EfiSystem,
        0x01 or 0x04 or 0x06 or 0x07 or 0x0B or 0x0C or 0x0E => PartitionKind.Basic,
        _ => PartitionKind.OemOrHidden,
    };

    public static string NormalizeGuid(string guid)
    {
        var trimmed = guid.Trim().Trim('{', '}').ToLowerInvariant();
        return "{" + trimmed + "}";
    }

    /// <summary>Shows only the last four characters, e.g. "••••3F2A". Serials identify hardware and are not needed in full.</summary>
    public static string? MaskSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return null;
        var s = serial.Trim().TrimEnd('.');
        return s.Length <= 4 ? "••••" : "••••" + s[^4..];
    }

    public static IReadOnlyList<PhysicalDisk> Map(
        IEnumerable<RawDisk> disks,
        IEnumerable<RawPartition> partitions,
        IEnumerable<RawVolume> volumes,
        IEnumerable<RawEncryption> encryption,
        bool encryptionKnown)
    {
        var volumesByPath = volumes
            .GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var encryptionByPath = encryption
            .GroupBy(e => e.VolumePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var partsByDisk = partitions.GroupBy(p => p.DiskNumber).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<PhysicalDisk>();
        foreach (var d in disks.OrderBy(d => d.Number))
        {
            var style = MapStyle(d.PartitionStyle);
            var raws = partsByDisk.TryGetValue(d.Number, out var list) ? list : [];
            var mapped = raws
                .OrderBy(p => p.Offset)
                .Select(p => MapPartition(p, style, volumesByPath, encryptionByPath, encryptionKnown))
                .ToList();
            mapped = MarkLogicalPartitions(mapped);

            result.Add(new PhysicalDisk(
                Number: checked((int)d.Number),
                FriendlyName: string.IsNullOrWhiteSpace(d.FriendlyName) ? $"Disk {d.Number}" : d.FriendlyName.Trim(),
                SerialNumber: d.SerialNumber?.Trim(),
                SizeBytes: checked((long)d.Size),
                LogicalSectorSize: checked((int)d.LogicalSectorSize),
                PhysicalSectorSize: checked((int)d.PhysicalSectorSize),
                Style: style,
                Bus: MapBus(d.BusType),
                IsBoot: d.IsBoot,
                IsSystem: d.IsSystem,
                IsOffline: d.IsOffline,
                IsReadOnly: d.IsReadOnly,
                Health: MapHealth(d.HealthStatus),
                Partitions: mapped));
        }
        return result;
    }

    private static Partition MapPartition(
        RawPartition p,
        PartitionStyle style,
        Dictionary<string, RawVolume> volumesByPath,
        Dictionary<string, RawEncryption> encryptionByPath,
        bool encryptionKnown)
    {
        var kind = style == PartitionStyle.Gpt ? ClassifyGpt(p.GptType) : ClassifyMbr(p.MbrType);

        Volume? volume = null;
        var volumePath = p.AccessPaths.FirstOrDefault(a => a.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
        if (volumePath is not null && volumesByPath.TryGetValue(volumePath, out var rv))
        {
            var enc = EncryptionState.Unknown;
            if (encryptionByPath.TryGetValue(volumePath, out var re))
            {
                enc = re.IsLocked == true ? EncryptionState.Locked
                    : re.ProtectionStatus == 1 || re.ConversionStatus is 1 or 2 or 3 or 4 or 5 ? EncryptionState.Encrypted
                    : re.ProtectionStatus == 0 && re.ConversionStatus is null or 0 ? EncryptionState.NotEncrypted
                    : EncryptionState.Unknown;
            }
            else if (encryptionKnown)
            {
                // Windows listed every encryptable volume and this one was not among them.
                enc = EncryptionState.NotEncrypted;
            }

            var size = checked((long)rv.Size);
            var free = Math.Min(size, checked((long)rv.SizeRemaining));
            volume = new Volume(
                DriveLetter: rv.DriveLetter is { } c && char.IsLetter(c) ? char.ToUpperInvariant(c).ToString() : null,
                Label: string.IsNullOrWhiteSpace(rv.FileSystemLabel) ? null : rv.FileSystemLabel,
                FileSystem: string.IsNullOrWhiteSpace(rv.FileSystem) ? null : rv.FileSystem.ToUpperInvariant(),
                SizeBytes: size,
                FreeBytes: free,
                Health: MapHealth(rv.HealthStatus),
                Encryption: enc);
        }

        return new Partition(
            DiskNumber: checked((int)p.DiskNumber),
            PartitionNumber: checked((int)p.PartitionNumber),
            OffsetBytes: checked((long)p.Offset),
            SizeBytes: checked((long)p.Size),
            Kind: kind,
            GptType: p.GptType is null ? null : NormalizeGuid(p.GptType),
            MbrType: p.MbrType,
            IsBoot: p.IsBoot,
            IsSystem: p.IsSystem,
            IsActive: p.IsActive,
            IsHidden: p.IsHidden,
            Volume: volume);
    }

    /// <summary>On MBR disks, partitions that sit inside an extended container are logical partitions.</summary>
    private static List<Partition> MarkLogicalPartitions(List<Partition> parts)
    {
        var containers = parts.Where(p => p.Kind == PartitionKind.Extended).ToList();
        if (containers.Count == 0) return parts;
        return parts.Select(p =>
            p.Kind != PartitionKind.Extended && containers.Any(c => p.OffsetBytes >= c.OffsetBytes && p.EndBytes <= c.EndBytes)
                ? p with { Kind = p.Kind == PartitionKind.Basic ? PartitionKind.Logical : p.Kind }
                : p).ToList();
    }
}
