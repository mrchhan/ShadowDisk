using System.Security.Cryptography;
using ShadowDisk.Core.Logging;

namespace ShadowDisk.Core.Copying;

// Stage 5, part 1. Copies every file from one folder (usually a drive's root) into an EMPTY
// destination folder and then proves the copy is identical by comparing SHA-256 checksums.
// The source is only ever read. Nothing is overwritten. Nothing is deleted.

public enum CopyPhase { Scanning, Copying, Verifying, Finished }

public sealed record CopyProgress(CopyPhase Phase, long FilesDone, long FilesTotal, long BytesDone, long BytesTotal, string? CurrentFile)
{
    public double Fraction => BytesTotal <= 0 ? (FilesTotal == 0 ? 1 : (double)FilesDone / FilesTotal) : Math.Min(1, (double)BytesDone / BytesTotal);
}

public enum CopyOutcome
{
    Verified,       // Every file copied and every checksum matches
    Problems,       // Finished, but some files failed or did not match — do NOT delete the source
    Cancelled,      // Stopped by the user; the source is untouched, the destination holds a partial copy
    Refused,        // Never started (e.g. destination not empty, not enough space)
}

public sealed record CopyReport(
    CopyOutcome Outcome,
    string Message,
    long FilesCopied,
    long BytesCopied,
    long FilesVerified,
    IReadOnlyList<string> Failures,     // relative path + reason
    IReadOnlyList<string> Skipped,      // relative path + reason (system folders, links)
    long FilesAlreadyCopied = 0)        // on resume: files that were already there and were not copied again
{
    /// <summary>True only when it is safe to treat the destination as a complete, identical copy.</summary>
    public bool IsVerified => Outcome == CopyOutcome.Verified;
}

public sealed class VerifiedCopy
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Folder names at the root of a Windows volume that belong to Windows, not the user.</summary>
    public static readonly string[] SkippedRootFolders = ["System Volume Information", "$RECYCLE.BIN", "$Recycle.Bin", "Recovery"];

    /// <summary>
    /// Marker file ShadowDisk puts in a copy folder while the copy is unfinished. It records the source,
    /// lets an interrupted copy be resumed, and is removed once every file is verified.
    /// </summary>
    public const string UnfinishedMarker = ".shadowdisk-copy-unfinished";

    /// <summary>True if <paramref name="folder"/> is an unfinished ShadowDisk copy of <paramref name="sourceRoot"/>.</summary>
    public static bool IsUnfinishedCopyOf(string folder, string sourceRoot)
    {
        var marker = Path.Combine(folder, UnfinishedMarker);
        if (!File.Exists(marker)) return false;
        try
        {
            return string.Equals(File.ReadAllText(marker).Trim(), Path.GetFullPath(sourceRoot), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly OperationLog? _log;
    private readonly Func<string, long?> _availableSpace;

    /// <param name="availableSpace">Free bytes at a path; injectable for tests. Defaults to the drive's free space.</param>
    public VerifiedCopy(OperationLog? log = null, Func<string, long?>? availableSpace = null)
    {
        _log = log;
        _availableSpace = availableSpace ?? DefaultAvailableSpace;
    }

    /// <summary>Hook for tests: called after a file is copied, before verification (e.g. to simulate corruption).</summary>
    internal Action<string>? AfterFileCopied { get; set; }

    /// <param name="resume">
    /// Continue an unfinished copy in <paramref name="destinationRoot"/> (it must hold ShadowDisk's marker for
    /// this source). Files already there with the same size and date are not copied again; partly copied files
    /// are copied again. Every file is still verified at the end.
    /// </param>
    public async Task<CopyReport> CopyAndVerifyAsync(string sourceRoot, string destinationRoot, IProgress<CopyProgress>? progress,
        CancellationToken ct, bool resume = false)
    {
        var failures = new List<string>();
        var skipped = new List<string>();

        var startedAt = DateTimeOffset.UtcNow;

        // ---------- Checks before anything is written ----------
        if (!Directory.Exists(sourceRoot))
            return Refuse($"The source folder {sourceRoot} does not exist.");
        var src = Path.GetFullPath(sourceRoot);
        var dst = Path.GetFullPath(destinationRoot);
        if (IsSameOrInside(dst, src) || IsSameOrInside(src, dst))
            return Refuse("The destination can't be inside the source (or the other way round).");
        if (resume)
        {
            if (!IsUnfinishedCopyOf(dst, src))
                return Refuse($"{dst} is not an unfinished ShadowDisk copy of {src}, so it can't be resumed.");
        }
        else if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst)
                     .Any(e => !SkippedRootFolders.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase)))
        {
            return Refuse($"The destination folder {dst} is not empty. ShadowDisk never overwrites files; choose an empty or new folder.");
        }

        progress?.Report(new CopyProgress(CopyPhase.Scanning, 0, 0, 0, 0, null));
        List<(string Relative, long Size)> files;
        List<string> folders;
        try
        {
            (files, folders) = await Task.Run(() => Scan(src, skipped, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return new CopyReport(CopyOutcome.Cancelled, "Cancelled while listing files. Nothing was copied.", 0, 0, 0, failures, skipped);
        }

        var totalBytes = files.Sum(f => f.Size);
        var alreadyThere = resume ? files.Where(f => IsAlreadyCopied(Path.Combine(src, f.Relative), Path.Combine(dst, f.Relative))).Sum(f => f.Size) : 0;
        var free = _availableSpace(dst);
        if (free is { } f && f < totalBytes - alreadyThere)
            return Refuse($"Not enough free space: {ByteSize.Format(totalBytes - alreadyThere)} still to copy, {ByteSize.Format(f)} free at the destination.");

        Log(resume
            ? $"Copy resumed: {files.Count} files, {ByteSize.Format(totalBytes)} ({ByteSize.Format(alreadyThere)} already copied)."
            : $"Copy started: {files.Count} files, {ByteSize.Format(totalBytes)}.");

        // ---------- Copy (hashing the source as it is read) ----------
        var sourceHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long filesDone = 0, bytesDone = 0, skippedExisting = 0;
        try
        {
            Directory.CreateDirectory(dst);
            if (!resume) await File.WriteAllTextAsync(Path.Combine(dst, UnfinishedMarker), src, ct);
            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(dst, folder));
            }

            foreach (var (relative, size) in files)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new CopyProgress(CopyPhase.Copying, filesDone, files.Count, bytesDone, totalBytes, relative));
                var from = Path.Combine(src, relative);
                var to = Path.Combine(dst, relative);
                try
                {
                    if (resume && File.Exists(to))
                    {
                        if (IsAlreadyCopied(from, to))
                        {
                            // Already there from the earlier run: read the original for its checksum, don't copy again.
                            sourceHashes[relative] = await HashFileAsync(from, ct);
                            skippedExisting++;
                            filesDone++;
                            bytesDone += size;
                            continue;
                        }
                        // Partly copied when the earlier run was cancelled: replace it (only inside ShadowDisk's own copy folder).
                        File.SetAttributes(to, FileAttributes.Normal);
                        File.Delete(to);
                    }
                    if (File.Exists(to)) throw new IOException("a file with this name already exists at the destination");
                    var hash = await CopyFileAsync(from, to, n => progress?.Report(new CopyProgress(CopyPhase.Copying, filesDone, files.Count, bytesDone + n, totalBytes, relative)), ct);
                    CopyMetadata(from, to);
                    sourceHashes[relative] = hash;
                    AfterFileCopied?.Invoke(to);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{relative}: could not be copied ({ex.Message})");
                }
                filesDone++;
                bytesDone += size;
            }
            foreach (var folder in folders) CopyFolderTimes(Path.Combine(src, folder), Path.Combine(dst, folder));
        }
        catch (OperationCanceledException)
        {
            Log("Copy cancelled by the user. The source was not changed.");
            return new CopyReport(CopyOutcome.Cancelled,
                $"Cancelled after {filesDone} of {files.Count} files. The source was not changed. Start again and choose Resume to continue where it stopped.",
                filesDone, bytesDone, 0, failures, skipped, skippedExisting);
        }

        // ---------- Verify: read every copied file back and compare checksums ----------
        long verified = 0, verifyBytes = 0;
        try
        {
            foreach (var (relative, size) in files)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new CopyProgress(CopyPhase.Verifying, verified, files.Count, verifyBytes, totalBytes, relative));
                verifyBytes += size;
                if (!sourceHashes.TryGetValue(relative, out var expected)) continue; // already listed as a failure
                try
                {
                    var actual = await HashFileAsync(Path.Combine(dst, relative), ct);
                    if (actual.AsSpan().SequenceEqual(expected)) verified++;
                    else failures.Add($"{relative}: the copy does not match the original (checksum differs)");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{relative}: the copy could not be read back ({ex.Message})");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new CopyReport(CopyOutcome.Cancelled,
                "Cancelled during verification. The source was not changed; the copy has NOT been verified. Choose Resume to finish it.",
                sourceHashes.Count, bytesDone, verified, failures, skipped, skippedExisting);
        }

        progress?.Report(new CopyProgress(CopyPhase.Finished, files.Count, files.Count, totalBytes, totalBytes, null));
        if (failures.Count == 0 && verified == files.Count)
        {
            try { File.Delete(Path.Combine(dst, UnfinishedMarker)); } catch (IOException) { }
            // On resume, files kept from an earlier run were only re-checked now, so the copy covers the state
            // as of when THIS run started; any later change is detected by DeleteSafety.
            VerifiedCopyRegistry.Record(new VerifiedCopyRecord(src, dst, startedAt, files.Count, totalBytes));
            var msg = $"All {files.Count} files ({ByteSize.Format(totalBytes)}) were copied and verified: every checksum matches." +
                      (skippedExisting > 0 ? $" {skippedExisting:N0} were already copied by the earlier run." : "");
            Log(msg);
            return new CopyReport(CopyOutcome.Verified, msg, sourceHashes.Count, bytesDone, verified, failures, skipped, skippedExisting);
        }

        var problem = $"{failures.Count} file(s) had problems. The copy is NOT complete; do not delete the original.";
        Log(problem, error: true);
        return new CopyReport(CopyOutcome.Problems, problem, sourceHashes.Count, bytesDone, verified, failures, skipped, skippedExisting);

        CopyReport Refuse(string message)
        {
            Log("Copy refused: " + message, error: true);
            return new CopyReport(CopyOutcome.Refused, message + " Nothing was copied.", 0, 0, 0, failures, skipped);
        }
    }

    private static (List<(string, long)> Files, List<string> Folders) Scan(string root, List<string> skipped, CancellationToken ct)
    {
        var files = new List<(string, long)>();
        var folders = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{Relative(root, dir)}: folder could not be read ({ex.Message})");
                continue;
            }

            foreach (var entry in entries)
            {
                var rel = Relative(root, entry);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add($"{rel}: could not be read ({ex.Message})");
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped.Add($"{rel}: link or junction (not followed; recreate it manually if needed)");
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (dir == root && SkippedRootFolders.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                    {
                        skipped.Add($"{rel}: Windows system folder (Windows recreates it)");
                        continue;
                    }
                    if (File.Exists(Path.Combine(entry, UnfinishedMarker)))
                    {
                        skipped.Add($"{rel}: unfinished ShadowDisk copy (not your data; resume or delete it)");
                        continue;
                    }
                    folders.Add(rel);
                    pending.Push(entry);
                }
                else
                {
                    files.Add((rel, new FileInfo(entry).Length));
                }
            }
        }
        folders.Sort(StringComparer.Ordinal);
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Item1, b.Item1));
        return (files, folders);
    }

    /// <summary>Same size and same modified time: the earlier run finished copying this file.</summary>
    private static bool IsAlreadyCopied(string from, string to)
    {
        try
        {
            var a = new FileInfo(from);
            var b = new FileInfo(to);
            return b.Exists && a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<byte[]> CopyFileAsync(string from, string to, Action<long> onBytes, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        await using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
        await using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
                onBytes(copied);
            }
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);   // make sure it is on the disk, not just in a cache
        }
        return sha.GetHashAndReset();
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private static void CopyMetadata(string from, string to)
    {
        try
        {
            var source = new FileInfo(from);
            File.SetCreationTimeUtc(to, source.CreationTimeUtc);
            File.SetLastWriteTimeUtc(to, source.LastWriteTimeUtc);
            File.SetLastAccessTimeUtc(to, source.LastAccessTimeUtc);
            if (OperatingSystem.IsWindows()) File.SetAttributes(to, source.Attributes & ~FileAttributes.Compressed & ~FileAttributes.Encrypted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { /* content is what matters */ }
    }

    private static void CopyFolderTimes(string from, string to)
    {
        try
        {
            Directory.SetCreationTimeUtc(to, Directory.GetCreationTimeUtc(from));
            Directory.SetLastWriteTimeUtc(to, Directory.GetLastWriteTimeUtc(from));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);

    private static bool IsSameOrInside(string path, string folder)
    {
        var f = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.StartsWith(f, cmp);
    }

    private static long? DefaultAvailableSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Log(string message, bool error = false)
    {
        if (_log is null) return;
        if (error) _log.Error(message); else _log.Info(message);
    }
}
