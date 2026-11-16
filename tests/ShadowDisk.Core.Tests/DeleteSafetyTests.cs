using ShadowDisk.Core.Copying;

namespace ShadowDisk.Core.Tests;

public sealed class DeleteSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shadowdisk-del-" + Guid.NewGuid().ToString("N"));

    public DeleteSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static VerifiedCopyRecord CopyAt(DateTimeOffset when) => new("X:\\", "Y:\\copy", when, 1, 1);

    [Fact]
    public void An_empty_volume_is_safe_to_delete()
    {
        Directory.CreateDirectory(Path.Combine(_root, "System Volume Information"));
        File.WriteAllText(Path.Combine(_root, "System Volume Information", "x"), "windows");
        Assert.Null(DeleteSafety.WhyNotSafeToDelete(_root, null));
    }

    [Fact]
    public void A_volume_with_files_and_no_verified_copy_is_not_safe()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "data");
        var reason = DeleteSafety.WhyNotSafeToDelete(_root, null);
        Assert.NotNull(reason);
        Assert.Contains("no verified copy", reason);
    }

    [Fact]
    public void A_verified_copy_with_no_later_changes_makes_it_safe()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "data");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "a.txt"), DateTime.UtcNow.AddHours(-2));
        File.SetCreationTimeUtc(Path.Combine(_root, "a.txt"), DateTime.UtcNow.AddHours(-2));
        Assert.Null(DeleteSafety.WhyNotSafeToDelete(_root, CopyAt(DateTimeOffset.UtcNow.AddMinutes(-1))));
    }

    [Fact]
    public void A_file_changed_after_the_copy_makes_it_unsafe()
    {
        File.WriteAllText(Path.Combine(_root, "new.txt"), "written after the copy");
        var reason = DeleteSafety.WhyNotSafeToDelete(_root, CopyAt(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.NotNull(reason);
        Assert.Contains("changed after", reason);
    }

    [Fact]
    public void A_missing_volume_is_not_safe()
    {
        Assert.NotNull(DeleteSafety.WhyNotSafeToDelete(Path.Combine(_root, "nope"), null));
    }

    [Fact]
    public void Registry_finds_copies_by_source_regardless_of_trailing_slash()
    {
        var rec = new VerifiedCopyRecord(_root, "dest", DateTimeOffset.UtcNow, 1, 1);
        VerifiedCopyRegistry.Record(rec);
        Assert.Same(rec, VerifiedCopyRegistry.Find(_root + Path.DirectorySeparatorChar));
        Assert.Null(VerifiedCopyRegistry.Find(Path.Combine(_root, "never-copied")));
    }
}
