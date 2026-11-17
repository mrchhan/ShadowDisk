using System.Text.Json;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Moving;

// "Move partition" — sector to sector, with power-loss protection.
//
// 1. The volume is locked and taken offline; NTFS's own map says which parts are in use. Only those parts move.
// 2. A journal is written on another drive, then the partition's type is changed to "ShadowDisk: moving", so Windows
//    will not mount it or run a disk check on a half-moved volume after a crash.
// 3. The used parts are copied in pieces no bigger than the move distance, in a safe order (towards the start:
//    first piece first; towards the end: last piece first). So a piece's original is never overwritten before that
//    piece has been written AND read back and compared. After every piece the journal records the progress.
// 4. The boot sector's "hidden sectors" field is updated, then the partition table gets the new start and the
//    original type back in one write, and Windows mounts the volume again.
// If the power fails at any point, the journal says exactly where to continue ("Resume move").

/// <summary>A range of bytes. Offsets are relative to the partition's start unless said otherwise.</summary>
public readonly record struct ByteRange(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>Raw access to the disk for one move. Implementations must refuse anything outside the move's area.</summary>
public interface IMoveDevice : IDisposable
{
    int SectorSize { get; }
    void Read(long diskOffset, Span<byte> buffer);
    void Write(long diskOffset, ReadOnlySpan<byte> data);

    /// <summary>Makes sure everything written so far is on the disk, not in a cache.</summary>
    void Flush();
}

public enum MovePhase { Hiding, Copying, Finishing }

/// <summary>Everything needed to finish or resume a move after a power cut. Saved after every piece.</summary>
public sealed class MoveJournal
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public int DiskNumber { get; set; }
    public string? DiskUniqueId { get; set; }
    public string? DiskLocation { get; set; }
    public PartitionStyle Style { get; set; }
    public int SectorSize { get; set; } = 512;
    public long OldOffset { get; set; }
    public long NewOffset { get; set; }
    public long SizeBytes { get; set; }
    public string? PartitionGuid { get; set; }
    public string? OriginalGptType { get; set; }
    public int? OriginalMbrType { get; set; }
    public string? DriveLetter { get; set; }
    public string? Label { get; set; }
    public long UsedBytes { get; set; }
    public int ChunkBytes { get; set; }
    public List<long[]> Ranges { get; set; } = [];
    public int NextChunk { get; set; }
    public MovePhase Phase { get; set; } = MovePhase.Hiding;

    public string Describe() =>
        $"{(DriveLetter is { Length: > 0 } l ? l + ":" : "A volume")} on Disk {DiskNumber}, moving {ByteSize.Format(SizeBytes)} " +
        $"from {ByteSize.Format(OldOffset)} to {ByteSize.Format(NewOffset)}";

    public IReadOnlyList<ByteRange> UsedRanges => Ranges.Select(r => new ByteRange(r[0], r[1])).ToList();
}

/// <summary>Keeps journals in a folder (on the drive Windows runs from, never on the disk area being moved).</summary>
public sealed class MoveJournalStore(string folder)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Folder { get; } = folder;

    private string PathOf(MoveJournal j) => Path.Combine(Folder, $"move-{j.Id}.json");

    /// <summary>Writes the journal safely: a new file, flushed, then swapped in, so a power cut never leaves half a journal.</summary>
    public void Save(MoveJournal j)
    {
        Directory.CreateDirectory(Folder);
        var target = PathOf(j);
        var temp = target + ".new";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, j, Options);
            stream.Flush(true);
        }
        File.Move(temp, target, overwrite: true);
    }

    public IReadOnlyList<MoveJournal> LoadAll()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<MoveJournal>();
        foreach (var file in Directory.EnumerateFiles(Folder, "move-*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<MoveJournal>(File.ReadAllText(file)) is { } j) list.Add(j);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return list;
    }

    public void Delete(MoveJournal j)
    {
        try { File.Delete(PathOf(j)); } catch (IOException) { }
    }
}

public sealed record MoveProgress(long BytesDone, long BytesTotal, double MiBPerSecond, TimeSpan? Remaining)
{
    public double Fraction => BytesTotal == 0 ? 1 : (double)BytesDone / BytesTotal;
}

public enum CopyOutcome { Done, Cancelled, Failed }

public static class MoveEngine
{
    public const int MaxChunkBytes = 8 * 1024 * 1024;

    /// <summary>Adds the boot sector and the NTFS backup boot sector (last sector) and merges overlapping ranges.</summary>
    public static List<ByteRange> Normalize(IEnumerable<ByteRange> used, long sizeBytes, int sectorSize)
    {
        var all = used.Append(new ByteRange(0, sectorSize)).Append(new ByteRange(sizeBytes - sectorSize, sectorSize))
            .Select(r =>
            {
                var start = Math.Max(0, r.Offset / sectorSize * sectorSize);
                var end = Math.Min(sizeBytes, (r.End + sectorSize - 1) / sectorSize * sectorSize);
                return new ByteRange(start, end - start);
            })
            .Where(r => r.Length > 0)
            .OrderBy(r => r.Offset)
            .ToList();
        var merged = new List<ByteRange>();
        foreach (var r in all)
        {
            if (merged.Count > 0 && r.Offset <= merged[^1].End)
                merged[^1] = new ByteRange(merged[^1].Offset, Math.Max(merged[^1].End, r.End) - merged[^1].Offset);
            else
                merged.Add(r);
        }
        return merged;
    }

    /// <summary>The chunk size for a move: never more than the distance moved, so no piece overwrites its own original.</summary>
    public static int ChunkSize(long distance, int sectorSize) =>
        (int)Math.Max(sectorSize, Math.Min(MaxChunkBytes, distance) / sectorSize * sectorSize);

    /// <summary>
    /// The pieces in the safe order: towards the start of the disk, from the first piece; towards the end, from the last.
    /// </summary>
    public static List<ByteRange> Chunks(IReadOnlyList<ByteRange> ranges, int chunkBytes, bool towardsStart)
    {
        var chunks = new List<ByteRange>();
        foreach (var r in ranges)
            for (var o = r.Offset; o < r.End; o += chunkBytes)
                chunks.Add(new ByteRange(o, Math.Min(chunkBytes, r.End - o)));
        if (!towardsStart) chunks.Reverse();
        return chunks;
    }

    /// <summary>
    /// Copies the remaining pieces, from <see cref="MoveJournal.NextChunk"/>. Each piece: read the original, write it at the
    /// new place, flush, read it back and compare; only then is the journal advanced. Stops (never retries) on a problem.
    /// </summary>
    public static (CopyOutcome Outcome, string Message) Copy(MoveJournal j, IMoveDevice device, MoveJournalStore store,
        IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        var chunks = Chunks(j.UsedRanges, j.ChunkBytes, j.NewOffset < j.OldOffset);
        var total = chunks.Sum(c => c.Length);
        var done = chunks.Take(j.NextChunk).Sum(c => c.Length);
        var buffer = new byte[j.ChunkBytes];
        var check = new byte[j.ChunkBytes];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var startDone = done;
        var lastReport = TimeSpan.Zero;

        var first = j.NextChunk;
        for (var k = j.NextChunk; k < chunks.Count; k++)
        {
            if (ct.IsCancellationRequested)
                return (CopyOutcome.Cancelled, $"Paused at {done * 100 / Math.Max(1, total)}%. The volume stays offline until the move is resumed.");
            var c = chunks[k];
            var len = (int)c.Length;
            try
            {
                device.Read(j.OldOffset + c.Offset, buffer.AsSpan(0, len));
                device.Write(j.NewOffset + c.Offset, buffer.AsSpan(0, len));
                device.Flush();
                device.Read(j.NewOffset + c.Offset, check.AsSpan(0, len));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return (CopyOutcome.Failed, $"The disk reported a problem at {ByteSize.Format(j.OldOffset + c.Offset)}: {ex.Message} " +
                                            "Nothing was retried. The move can be resumed from this piece.");
            }
            if (!buffer.AsSpan(0, len).SequenceEqual(check.AsSpan(0, len)))
                return (CopyOutcome.Failed, $"A piece read back differently from what was written (at {ByteSize.Format(j.NewOffset + c.Offset)}). " +
                                            "The drive may be failing. The original of that piece is untouched; the move stopped there.");

            done += len;
            j.NextChunk = k + 1;
            store.Save(j);

            if (progress is not null && (k == first || k == chunks.Count - 1 || clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(300)))
            {
                lastReport = clock.Elapsed;
                var rate = (done - startDone) / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                progress.Report(new MoveProgress(done, total, rate / ByteSize.MiB, rate > 0 ? TimeSpan.FromSeconds((total - done) / rate) : null));
            }
        }
        return (CopyOutcome.Done, $"All {ByteSize.Format(total)} of used data were moved, and every piece was read back and matched.");
    }

    /// <summary>
    /// NTFS stores the partition's start (in sectors) at byte 0x1C of its boot sector and of its backup at the end.
    /// Updates both at the new place when they held the old start. Safe to run again.
    /// </summary>
    public static void PatchBootSectors(MoveJournal j, IMoveDevice device)
    {
        var sector = new byte[j.SectorSize];
        var oldStart = (uint)(j.OldOffset / j.SectorSize);
        var newStart = (uint)(j.NewOffset / j.SectorSize);
        foreach (var at in new[] { j.NewOffset, j.NewOffset + j.SizeBytes - j.SectorSize })
        {
            device.Read(at, sector);
            var isNtfs = sector.AsSpan(3, 4).SequenceEqual("NTFS"u8);
            if (!isNtfs) continue;
            var hidden = BitConverter.ToUInt32(sector, 0x1C);
            if (hidden != oldStart) continue;
            BitConverter.TryWriteBytes(sector.AsSpan(0x1C, 4), newStart);
            device.Write(at, sector);
        }
        device.Flush();
    }
}
