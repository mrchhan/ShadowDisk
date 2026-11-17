namespace ShadowDisk.Core.Moving;

/// <summary>
/// Reads NTFS's map of used clusters straight from the disk (boot sector → $MFT → record 6 "$Bitmap" → its data runs).
/// Used when Windows won't answer while the volume is locked: after a lock and dismount, the on-disk $Bitmap is final,
/// because Windows has flushed everything. Read-only.
/// </summary>
public static class NtfsBitmap
{
    /// <summary>Reads <paramref name="buffer"/>.Length bytes at a partition-relative, sector-aligned offset.</summary>
    public delegate void ReadAt(long offset, Span<byte> buffer);

    private const int FixupStride = 512;
    private const int BitmapRecord = 6;

    public static (List<ByteRange>? Used, string? Error) Read(ReadAt read, int sectorSize, long partitionSize)
    {
        try
        {
            var boot = ReadAligned(read, sectorSize, 0, 512);
            if (!boot.AsSpan(3, 4).SequenceEqual("NTFS"u8)) return (null, "the boot sector does not say NTFS");
            int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
            int spcRaw = boot[0x0D];
            long sectorsPerCluster = spcRaw <= 0x80 ? spcRaw : 1L << (256 - spcRaw);
            var clusterSize = bytesPerSector * sectorsPerCluster;
            var totalSectors = BitConverter.ToInt64(boot, 0x28);
            var mftLcn = BitConverter.ToInt64(boot, 0x30);
            var recRaw = unchecked((sbyte)boot[0x40]);
            var recordSize = recRaw > 0 ? recRaw * clusterSize : 1L << -recRaw;
            if (bytesPerSector < 512 || clusterSize <= 0 || clusterSize > 2 * 1024 * 1024 || recordSize < 512 || recordSize > 65536
                || totalSectors <= 0 || totalSectors * bytesPerSector > partitionSize || mftLcn <= 0)
                return (null, "the NTFS boot sector has values that don't make sense");
            var totalClusters = totalSectors / sectorsPerCluster;

            var record = ReadAligned(read, sectorSize, mftLcn * clusterSize + BitmapRecord * recordSize, (int)recordSize);
            if (!record.AsSpan(0, 4).SequenceEqual("FILE"u8)) return (null, "the $Bitmap record was not found");
            if (!ApplyFixups(record)) return (null, "the $Bitmap record is damaged (its check values don't match)");

            var runs = FindUnnamedDataRuns(record, out var realSize);
            if (runs is null) return (null, "the $Bitmap record has no data list");
            var bits = Math.Min(totalClusters, realSize * 8);

            var used = new List<ByteRange>();
            long bitIndex = 0;
            long? runStart = null;
            const int Piece = 4 * 1024 * 1024;
            foreach (var (lcn, count) in runs)
            {
                var bytesLeft = count * clusterSize;
                var at = lcn * clusterSize;
                while (bytesLeft > 0 && bitIndex < bits)
                {
                    var n = (int)Math.Min(Piece, bytesLeft);
                    if (at < 0 || at + n > partitionSize) return (null, "the $Bitmap data points outside the partition");
                    var data = ReadAligned(read, sectorSize, at, n);
                    for (var i = 0; i < n && bitIndex < bits; i++)
                    {
                        var b = data[i];
                        for (var k = 0; k < 8 && bitIndex < bits; k++, bitIndex++)
                        {
                            var isUsed = (b & (1 << k)) != 0;
                            if (isUsed && runStart is null) runStart = bitIndex;
                            else if (!isUsed && runStart is { } s)
                            {
                                used.Add(new ByteRange(s * clusterSize, (bitIndex - s) * clusterSize));
                                runStart = null;
                            }
                        }
                    }
                    at += n;
                    bytesLeft -= n;
                }
            }
            if (bitIndex < bits) return (null, "the $Bitmap is shorter than the volume");
            if (runStart is { } open) used.Add(new ByteRange(open * clusterSize, (bitIndex - open) * clusterSize));
            return (used, null);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or IOException)
        {
            return (null, ex.Message);
        }
    }

    private static byte[] ReadAligned(ReadAt read, int sectorSize, long offset, int length)
    {
        var start = offset / sectorSize * sectorSize;
        var end = (offset + length + sectorSize - 1) / sectorSize * sectorSize;
        var buffer = new byte[end - start];
        read(start, buffer);
        return buffer.AsSpan((int)(offset - start), length).ToArray();
    }

    /// <summary>NTFS "update sequence" check: the last two bytes of every 512 bytes hold a check value; put the real bytes back.</summary>
    internal static bool ApplyFixups(byte[] record)
    {
        int usaOffset = BitConverter.ToUInt16(record, 4);
        int usaCount = BitConverter.ToUInt16(record, 6);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length || (usaCount - 1) * FixupStride > record.Length) return false;
        var check = BitConverter.ToUInt16(record, usaOffset);
        for (var i = 1; i < usaCount; i++)
        {
            var at = i * FixupStride - 2;
            if (BitConverter.ToUInt16(record, at) != check) return false;
            record[at] = record[usaOffset + i * 2];
            record[at + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>The (LCN, cluster count) runs of the unnamed non-resident $DATA attribute.</summary>
    internal static List<(long Lcn, long Count)>? FindUnnamedDataRuns(byte[] record, out long realSize)
    {
        realSize = 0;
        int at = BitConverter.ToUInt16(record, 0x14);
        while (at + 16 <= record.Length)
        {
            var type = BitConverter.ToUInt32(record, at);
            if (type == 0xFFFFFFFF) break;
            var length = (int)BitConverter.ToUInt32(record, at + 4);
            if (length < 16 || at + length > record.Length) return null;
            if (type == 0x80 && record[at + 9] == 0 && record[at + 8] == 1)
            {
                realSize = BitConverter.ToInt64(record, at + 0x30);
                int runsAt = at + BitConverter.ToUInt16(record, at + 0x20);
                var runs = new List<(long, long)>();
                long lcn = 0;
                while (runsAt < at + length && record[runsAt] != 0)
                {
                    var header = record[runsAt++];
                    int lenSize = header & 0x0F, offSize = header >> 4;
                    if (lenSize == 0 || lenSize > 8 || offSize > 8) return null;
                    long count = 0;
                    for (var i = 0; i < lenSize; i++) count |= (long)record[runsAt + i] << (8 * i);
                    runsAt += lenSize;
                    if (offSize == 0) return null;   // sparse: never for $Bitmap
                    long delta = 0;
                    for (var i = 0; i < offSize; i++) delta |= (long)record[runsAt + i] << (8 * i);
                    if ((record[runsAt + offSize - 1] & 0x80) != 0 && offSize < 8) delta -= 1L << (8 * offSize);   // signed
                    runsAt += offSize;
                    lcn += delta;
                    runs.Add((lcn, count));
                }
                return runs;
            }
            at += length;
        }
        return null;
    }
}
