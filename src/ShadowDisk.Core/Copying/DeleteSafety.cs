namespace ShadowDisk.Core.Copying;

/// <summary>Verified copies made during this run of ShadowDisk, by source folder (e.g. "G:\").</summary>
public static class VerifiedCopyRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, VerifiedCopyRecord> Records = new(StringComparer.OrdinalIgnoreCase);

    public static void Record(VerifiedCopyRecord record)
    {
        lock (Gate) Records[Normalize(record.SourceRoot)] = record;
    }

    public static VerifiedCopyRecord? Find(string sourceRoot)
    {
        lock (Gate) return Records.TryGetValue(Normalize(sourceRoot), out var r) ? r : null;
    }

    public static void Clear()
    {
        lock (Gate) Records.Clear();
    }

    private static string Normalize(string path)
    {
        var p = Path.GetFullPath(path);
        return p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
    }
}

/// <param name="StartedAt">When the copy began. Any file changed after this is NOT covered by the copy.</param>
public sealed record VerifiedCopyRecord(string SourceRoot, string DestinationFolder, DateTimeOffset StartedAt, long Files, long Bytes);

/// <summary>
/// Decides whether a volume's files are safe to lose. A volume may only be deleted when it holds no user
/// files, or when a verified copy was made in this session and nothing on the volume has changed since.
/// </summary>
public static class DeleteSafety
{
    /// <summary>Returns null when deleting is safe, otherwise the reason it is not.</summary>
    public static string? WhyNotSafeToDelete(string volumeRoot, VerifiedCopyRecord? copy, CancellationToken ct = default)
    {
        if (!Directory.Exists(volumeRoot))
            return $"{volumeRoot} can't be read, so ShadowDisk can't check what is on it.";

        long files = 0;
        string? changed = null;
        var cutoff = copy?.StartedAt.UtcDateTime;
        var pending = new Stack<string>();
        pending.Push(volumeRoot);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Part of the volume can't be read ({Path.GetRelativePath(volumeRoot, dir)}), so ShadowDisk can't be sure what is on it.";
            }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{entry} can't be read."; }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (dir == volumeRoot && VerifiedCopy.SkippedRootFolders.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                        continue;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        files++;
                        continue;
                    }
                    if (cutoff is { } c1 && Directory.GetCreationTimeUtc(entry) > c1) changed ??= Path.GetRelativePath(volumeRoot, entry);
                    pending.Push(entry);
                    continue;
                }

                files++;
                if (cutoff is { } c)
                {
                    var info = new FileInfo(entry);
                    if (info.LastWriteTimeUtc > c || info.CreationTimeUtc > c) changed ??= Path.GetRelativePath(volumeRoot, entry);
                }
            }
        }

        if (files == 0) return null;   // empty volume: nothing to lose
        if (copy is null)
            return $"The volume holds {files:N0} file(s) and no verified copy of it was made in this session. " +
                   "Use “Copy & verify…” first; ShadowDisk only deletes volumes whose files are safely copied.";
        if (changed is not null)
            return $"Files on the volume changed after the verified copy started (for example {changed}). Make a new verified copy first.";
        return null;
    }
}
