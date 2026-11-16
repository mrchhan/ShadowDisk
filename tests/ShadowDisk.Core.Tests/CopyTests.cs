using ShadowDisk.Core.Copying;
using ShadowDisk.Core.Logging;

namespace ShadowDisk.Core.Tests;

public sealed class CopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shadowdisk-copy-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Destination => Path.Combine(_root, "dest");

    public CopyTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "Photos", "2026"));
        Directory.CreateDirectory(Path.Combine(Source, "Empty folder"));
        File.WriteAllText(Path.Combine(Source, "notes.txt"), "hello");
        File.WriteAllBytes(Path.Combine(Source, "Photos", "2026", "big.bin"), Enumerable.Range(0, 3_000_000).Select(i => (byte)(i % 251)).ToArray());
        File.WriteAllBytes(Path.Combine(Source, "Photos", "empty.dat"), []);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Copies_everything_and_verifies_every_checksum()
    {
        var log = new OperationLog(null);
        var report = await new VerifiedCopy(log).CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None);

        Assert.Equal(CopyOutcome.Verified, report.Outcome);
        Assert.True(report.IsVerified);
        Assert.Equal(3, report.FilesCopied);
        Assert.Equal(3, report.FilesVerified);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(Destination, "notes.txt")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "Photos", "2026", "big.bin")),
                     File.ReadAllBytes(Path.Combine(Destination, "Photos", "2026", "big.bin")));
        Assert.True(Directory.Exists(Path.Combine(Destination, "Empty folder")));
        Assert.Contains(log.Entries, e => e.Message.Contains("verified"));
    }

    [Fact]
    public async Task Source_is_never_changed()
    {
        var before = Directory.GetFiles(Source, "*", SearchOption.AllDirectories).Select(f => (f, File.ReadAllBytes(f).Length, File.GetLastWriteTimeUtc(f))).ToList();
        await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None);
        var after = Directory.GetFiles(Source, "*", SearchOption.AllDirectories).Select(f => (f, File.ReadAllBytes(f).Length, File.GetLastWriteTimeUtc(f))).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Preserves_file_timestamps()
    {
        var stamp = new DateTime(2020, 5, 17, 10, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(Source, "notes.txt"), stamp);
        await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(Destination, "notes.txt")));
    }

    [Fact]
    public async Task A_corrupted_copy_is_caught_by_verification()
    {
        var copier = new VerifiedCopy { AfterFileCopied = path => { if (path.EndsWith("notes.txt")) File.WriteAllText(path, "HELLO"); } };
        var report = await copier.CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None);
        Assert.Equal(CopyOutcome.Problems, report.Outcome);
        Assert.False(report.IsVerified);
        Assert.Contains(report.Failures, f => f.Contains("notes.txt") && f.Contains("checksum"));
        Assert.Contains("do not delete the original", report.Message);
    }

    [Fact]
    public async Task Never_overwrites_a_non_empty_destination()
    {
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Destination, "keep-me.txt"), "important");
        var report = await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None);
        Assert.Equal(CopyOutcome.Refused, report.Outcome);
        Assert.Equal("important", File.ReadAllText(Path.Combine(Destination, "keep-me.txt")));
        Assert.False(File.Exists(Path.Combine(Destination, "notes.txt")));
    }

    [Fact]
    public async Task Refuses_when_there_is_not_enough_space()
    {
        var report = await new VerifiedCopy(availableSpace: _ => 10).CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None);
        Assert.Equal(CopyOutcome.Refused, report.Outcome);
        Assert.Contains("Not enough free space", report.Message);
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task Refuses_a_destination_inside_the_source()
    {
        var report = await new VerifiedCopy().CopyAndVerifyAsync(Source, Path.Combine(Source, "copy"), null, CancellationToken.None);
        Assert.Equal(CopyOutcome.Refused, report.Outcome);
    }

    [Fact]
    public async Task Cancelling_stops_and_leaves_the_source_alone()
    {
        using var cts = new CancellationTokenSource();
        var copier = new VerifiedCopy { AfterFileCopied = _ => cts.Cancel() };
        var report = await copier.CopyAndVerifyAsync(Source, Destination, null, cts.Token);
        Assert.Equal(CopyOutcome.Cancelled, report.Outcome);
        Assert.False(report.IsVerified);
        Assert.True(File.Exists(Path.Combine(Source, "notes.txt")));
    }

    [Fact]
    public async Task Skips_windows_system_folders_at_the_root()
    {
        Directory.CreateDirectory(Path.Combine(Source, "System Volume Information"));
        File.WriteAllText(Path.Combine(Source, "System Volume Information", "tracking.log"), "x");
        var report = await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None);
        Assert.True(report.IsVerified);
        Assert.Contains(report.Skipped, s => s.Contains("System Volume Information"));
        Assert.False(Directory.Exists(Path.Combine(Destination, "System Volume Information")));
    }

    [Fact]
    public async Task Links_are_listed_as_skipped_not_followed()
    {
        var link = Path.Combine(Source, "shortcut-to-photos");
        try { Directory.CreateSymbolicLink(link, Path.Combine(Source, "Photos")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        var report = await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None);
        Assert.True(report.IsVerified);
        Assert.Contains(report.Skipped, s => s.Contains("shortcut-to-photos") && s.Contains("link"));
        Assert.Equal(3, report.FilesCopied);   // the linked folder's files are not copied twice
    }

    [Fact]
    public async Task Reports_progress_through_every_phase()
    {
        var phases = new List<CopyPhase>();
        var progress = new SynchronousProgress<CopyProgress>(p => phases.Add(p.Phase));
        await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, progress, CancellationToken.None);
        Assert.Contains(CopyPhase.Scanning, phases);
        Assert.Contains(CopyPhase.Copying, phases);
        Assert.Contains(CopyPhase.Verifying, phases);
        Assert.Equal(CopyPhase.Finished, phases[^1]);
    }

    [Fact]
    public async Task Resume_continues_an_unfinished_copy_without_copying_everything_again()
    {
        using var cts = new CancellationTokenSource();
        var count = 0;
        var first = new VerifiedCopy { AfterFileCopied = _ => { if (++count == 1) cts.Cancel(); } };
        var cancelled = await first.CopyAndVerifyAsync(Source, Destination, null, cts.Token);
        Assert.Equal(CopyOutcome.Cancelled, cancelled.Outcome);
        Assert.True(VerifiedCopy.IsUnfinishedCopyOf(Destination, Source));

        var resumed = await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None, resume: true);
        Assert.Equal(CopyOutcome.Verified, resumed.Outcome);
        Assert.Equal(1, resumed.FilesAlreadyCopied);          // the first file was not copied again
        Assert.Equal(3, resumed.FilesVerified);               // but every file was still verified
        Assert.False(File.Exists(Path.Combine(Destination, VerifiedCopy.UnfinishedMarker)));
    }

    [Fact]
    public async Task Resume_replaces_a_partly_copied_file()
    {
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Destination, VerifiedCopy.UnfinishedMarker), Path.GetFullPath(Source));
        Directory.CreateDirectory(Path.Combine(Destination, "Photos", "2026"));
        File.WriteAllBytes(Path.Combine(Destination, "Photos", "2026", "big.bin"), new byte[1000]);   // half-written

        var report = await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None, resume: true);
        Assert.Equal(CopyOutcome.Verified, report.Outcome);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "Photos", "2026", "big.bin")),
                     File.ReadAllBytes(Path.Combine(Destination, "Photos", "2026", "big.bin")));
    }

    [Fact]
    public async Task Resume_is_refused_for_a_folder_that_is_not_an_unfinished_copy()
    {
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Destination, "mine.txt"), "keep");
        var report = await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None, resume: true);
        Assert.Equal(CopyOutcome.Refused, report.Outcome);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Destination, "mine.txt")));
    }

    [Fact]
    public async Task Unfinished_copy_folders_inside_the_source_are_not_copied()
    {
        var old = Path.Combine(Source, "ShadowDisk copy of X");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, VerifiedCopy.UnfinishedMarker), "elsewhere");
        File.WriteAllText(Path.Combine(old, "partial.bin"), "x");
        var report = await new VerifiedCopy().CopyAndVerifyAsync(Source, Destination, null, CancellationToken.None);
        Assert.True(report.IsVerified);
        Assert.Contains(report.Skipped, s => s.Contains("unfinished ShadowDisk copy"));
        Assert.False(Directory.Exists(Path.Combine(Destination, "ShadowDisk copy of X")));
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
