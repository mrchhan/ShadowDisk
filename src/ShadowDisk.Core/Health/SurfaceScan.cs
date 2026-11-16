using System.Diagnostics;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Health;

// Stage 4: read-only surface scan.
//
// Reads the whole disk once, block by block, and times each read. It NEVER writes, and it never
// re-reads a block that failed (re-reading a weak area is what stresses a failing drive most).
// A block that can't be read is "Unreadable"; one that takes long to read is "Slow" (often a sign the
// drive is struggling with that area). If many blocks are unreadable the scan stops by itself and
// recommends backing up instead of scanning further.

/// <summary>Reads raw bytes from a disk. Implementations must open the disk read-only.</summary>
public interface IRawDiskReader : IDisposable
{
    long SizeBytes { get; }
    int SectorSize { get; }

    /// <summary>Reads exactly <paramref name="length"/> bytes at <paramref name="offset"/>. Returns null on success, or the error.</summary>
    string? Read(long offset, int length);
}

public enum BlockState : byte { NotScanned, Good, Slow, Unreadable }
public enum ScanProblemKind { Slow, Unreadable }
public enum ScanStatus { Completed, Cancelled, StoppedTooManyErrors, Failed }

public sealed record ScanProblem(long OffsetBytes, long LengthBytes, ScanProblemKind Kind, long Milliseconds, string? Error);

public sealed record ScanProgress(long BytesDone, long TotalBytes, double MiBPerSecond, int Unreadable, int Slow, TimeSpan? Remaining)
{
    public double Fraction => TotalBytes == 0 ? 1 : (double)BytesDone / TotalBytes;
}

public sealed record ScanResult(ScanStatus Status, long ScannedUpToBytes, long TotalBytes, TimeSpan Elapsed,
    IReadOnlyList<ScanProblem> Problems, string Message);

/// <summary>A coarse picture of the disk for the screen: each bucket shows the worst state of the blocks in it.</summary>
public sealed class ScanMap(long totalBytes, int buckets = 400)
{
    private readonly BlockState[] _states = new BlockState[buckets];

    public long TotalBytes { get; } = totalBytes;
    public int Count => _states.Length;
    public BlockState this[int i] => _states[i];

    public void Mark(long offset, long length, BlockState state)
    {
        if (TotalBytes <= 0) return;
        var first = (int)Math.Min(Count - 1, offset * Count / TotalBytes);
        var last = (int)Math.Min(Count - 1, Math.Max(offset, offset + length - 1) * Count / TotalBytes);
        for (var i = first; i <= last; i++)
            if (state > _states[i]) _states[i] = state;
    }

    public BlockState[] Snapshot() => (BlockState[])_states.Clone();

    public void Clear() => Array.Clear(_states);
}

public sealed class ScanOptions
{
    public int BlockBytes { get; init; } = 1024 * 1024;
    public long SlowMilliseconds { get; init; } = 500;
    public int StopAfterUnreadable { get; init; } = 20;
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

public static class SurfaceScanner
{
    /// <summary>
    /// Scans from <paramref name="startOffset"/> to the end (use a previous result's ScannedUpToBytes to continue).
    /// Problems found are added to <paramref name="problems"/> and <paramref name="map"/>, so a continued scan keeps them.
    /// Runs on the calling thread; call it from a background task.
    /// </summary>
    public static ScanResult Scan(IRawDiskReader reader, long startOffset, ScanMap map, List<ScanProblem> problems,
        ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var total = reader.SizeBytes;
        var sector = Math.Max(512, reader.SectorSize);
        var block = Math.Max(sector, options.BlockBytes / sector * sector);
        var offset = Math.Clamp(startOffset / sector * sector, 0, total);
        var started = offset;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var unreadable = problems.Count(p => p.Kind == ScanProblemKind.Unreadable);
        var slow = problems.Count(p => p.Kind == ScanProblemKind.Slow);

        ScanResult Finish(ScanStatus status, string message) =>
            new(status, offset, total, clock.Elapsed, problems.ToList(), message);

        void Report(bool force)
        {
            if (progress is null || (!force && clock.Elapsed - lastReport < options.ProgressInterval)) return;
            lastReport = clock.Elapsed;
            var seconds = Math.Max(0.001, clock.Elapsed.TotalSeconds);
            var rate = (offset - started) / seconds;
            TimeSpan? remaining = rate > 0 ? TimeSpan.FromSeconds((total - offset) / rate) : null;
            progress.Report(new ScanProgress(offset, total, rate / (1024 * 1024), unreadable, slow, remaining));
        }

        try
        {
            while (offset < total)
            {
                if (ct.IsCancellationRequested)
                {
                    Report(true);
                    return Finish(ScanStatus.Cancelled, $"Stopped at {ByteSize.Format(offset)} of {ByteSize.Format(total)}. You can continue from there.");
                }

                var length = (int)Math.Min(block, (total - offset) / sector * sector);
                if (length <= 0) { offset = total; break; }

                var t0 = clock.ElapsedMilliseconds;
                var error = reader.Read(offset, length);
                var ms = clock.ElapsedMilliseconds - t0;

                if (error is not null)
                {
                    unreadable++;
                    problems.Add(new ScanProblem(offset, length, ScanProblemKind.Unreadable, ms, error));
                    map.Mark(offset, length, BlockState.Unreadable);
                }
                else if (ms >= options.SlowMilliseconds)
                {
                    slow++;
                    problems.Add(new ScanProblem(offset, length, ScanProblemKind.Slow, ms, null));
                    map.Mark(offset, length, BlockState.Slow);
                }
                else
                {
                    map.Mark(offset, length, BlockState.Good);
                }
                offset += length;
                Report(false);

                if (unreadable >= options.StopAfterUnreadable)
                {
                    Report(true);
                    return Finish(ScanStatus.StoppedTooManyErrors,
                        $"Stopped: {unreadable} areas could not be read. Scanning a failing drive further can make it worse. " +
                        "Copy your important files from this disk to another drive now.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Report(true);
            return Finish(ScanStatus.Failed, $"The scan could not continue: {ex.Message}");
        }

        Report(true);
        offset = total;
        return Finish(ScanStatus.Completed, unreadable == 0 && slow == 0
            ? "Finished. Every part of the disk could be read, and none was slow."
            : $"Finished. {unreadable} unreadable and {slow} slow area(s) were found.");
    }

    /// <summary>Which partition (or free space) a disk position belongs to, in plain words.</summary>
    public static string WhereIs(PhysicalDisk disk, long offset)
    {
        var p = disk.Partitions.FirstOrDefault(x => offset >= x.OffsetBytes && offset < x.EndBytes);
        return p is null ? "not in any partition" : $"in {p.DisplayName}";
    }

    /// <summary>A plain-text summary of a scan, for saving.</summary>
    public static string Describe(PhysicalDisk disk, ScanResult result)
    {
        var lines = new List<string>
        {
            $"ShadowDisk surface scan (read-only) — Disk {disk.Number}: {disk.FriendlyName}",
            $"Scanned {ByteSize.Format(result.ScannedUpToBytes)} of {ByteSize.Format(result.TotalBytes)} in {result.Elapsed:hh\\:mm\\:ss}.",
            result.Message,
            "",
        };
        if (result.Problems.Count == 0) lines.Add("No problems found.");
        foreach (var p in result.Problems)
            lines.Add(p.Kind == ScanProblemKind.Unreadable
                ? $"UNREADABLE  at {ByteSize.Format(p.OffsetBytes)} ({p.OffsetBytes:N0} bytes), {WhereIs(disk, p.OffsetBytes)}: {p.Error}"
                : $"SLOW        at {ByteSize.Format(p.OffsetBytes)} ({p.OffsetBytes:N0} bytes), {WhereIs(disk, p.OffsetBytes)}: {p.Milliseconds:N0} ms");
        lines.Add("");
        lines.Add("A scan reads the disk once. It does not repair anything, and a clean result does not guarantee the disk won't fail.");
        return string.Join(Environment.NewLine, lines);
    }
}
