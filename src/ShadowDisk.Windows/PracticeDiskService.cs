using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using ShadowDisk.Core.Operations;

namespace ShadowDisk.Windows;

/// <summary>
/// Creates and removes ShadowDisk practice disks: small virtual disk files (.vhd) kept in
/// %LOCALAPPDATA%\ShadowDisk\practice. Windows treats an attached file like a real drive, so
/// operations can be tried without any risk to the PC's real disks.
///
/// Uses Windows' own diskpart tool, which selects the virtual disk by its FILE PATH only. These
/// scripts never select a disk by number, so they cannot touch any other disk. Requires admin.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PracticeDiskService
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadowDisk", "practice");

    public static OperationPolicy Policy { get; } = new(Folder);

    public IReadOnlyList<string> ListFiles() =>
        Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*" + OperationPolicy.PracticeFileExtension).Order().ToList()
            : [];

    /// <summary>Creates, attaches and initialises (GPT) a new practice disk. Returns its file path.</summary>
    public async Task<string> CreateAsync(int sizeMiB, IProgress<string>? progress = null)
    {
        if (!Elevation.IsElevated()) throw new InvalidOperationException("Creating a practice disk needs administrator rights. Use “Restart as administrator”.");
        if (sizeMiB is < 64 or > 16384) throw new ArgumentOutOfRangeException(nameof(sizeMiB), "Practice disks must be between 64 MiB and 16 GiB.");

        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"practice-{DateTime.Now:yyyyMMdd-HHmmss}{OperationPolicy.PracticeFileExtension}");

        progress?.Report("Creating the practice disk file…");
        await RunDiskPartAsync(
            $"create vdisk file=\"{path}\" maximum={sizeMiB.ToString(CultureInfo.InvariantCulture)} type=expandable",
            $"select vdisk file=\"{path}\"",
            "attach vdisk");

        progress?.Report("Waiting for Windows to show the practice disk…");
        var number = await WaitForDiskAsync(path);

        progress?.Report("Initialising the practice disk (GPT)…");
        await Task.Run(() => PrepareDisk(number, path));
        return path;
    }

    /// <summary>Detaches a practice disk and deletes its file. Only files inside the practice folder are accepted.</summary>
    public async Task RemoveAsync(string path)
    {
        if (!Elevation.IsElevated()) throw new InvalidOperationException("Removing a practice disk needs administrator rights. Use “Restart as administrator”.");
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !full.EndsWith(OperationPolicy.PracticeFileExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only ShadowDisk practice disk files can be removed.");

        // Detaching a disk that is not attached reports an error; that's fine, the file is deleted next.
        try { await RunDiskPartAsync($"select vdisk file=\"{full}\"", "detach vdisk"); }
        catch (InvalidOperationException) { }
        File.Delete(full);
    }

    private static async Task<int> WaitForDiskAsync(string path)
    {
        for (var i = 0; i < 40; i++)
        {
            var number = await Task.Run(() =>
            {
                var scope = Wmi.StorageScope();
                var disk = Wmi.All(scope, "MSFT_Disk").FirstOrDefault(d =>
                    string.Equals((d["Location"] as string)?.Trim(), path, StringComparison.OrdinalIgnoreCase));
                return disk is null ? -1 : Wmi.ToInt(disk["Number"]);
            });
            if (number >= 0) return number;
            await Task.Delay(500);
        }
        throw new InvalidOperationException("Windows attached the practice disk but did not list it within 20 seconds. Use Refresh, or remove it and try again.");
    }

    /// <summary>Brings the NEW practice disk online and initialises it as GPT. Re-checks it is the practice file first.</summary>
    private static void PrepareDisk(int number, string path)
    {
        var scope = Wmi.StorageScope();
        using var disk = Wmi.Disk(scope, number) ?? throw new InvalidOperationException("The practice disk disappeared.");
        if (!string.Equals((disk["Location"] as string)?.Trim(), path, StringComparison.OrdinalIgnoreCase) || Wmi.ToUShort(disk["BusType"]) != 15)
            throw new InvalidOperationException("Safety check failed: the disk found is not the new practice disk. Nothing was initialised.");

        if (Wmi.ToBool(disk["IsOffline"]))
        {
            var (code, _) = Wmi.Invoke(disk, "Online");
            if (code != 0) throw new InvalidOperationException("Could not bring the practice disk online: " + Wmi.Explain(code));
        }
        if (Wmi.ToBool(disk["IsReadOnly"]))
        {
            var (code, _) = Wmi.Invoke(disk, "SetAttributes", p => p["IsReadOnly"] = false);
            if (code != 0) throw new InvalidOperationException("Could not make the practice disk writable: " + Wmi.Explain(code));
        }
        if (Wmi.ToUShort(disk["PartitionStyle"]) == 0)
        {
            var (code, _) = Wmi.Invoke(disk, "Initialize", p => p["PartitionStyle"] = (ushort)2);
            if (code != 0) throw new InvalidOperationException("Could not initialise the practice disk: " + Wmi.Explain(code));
        }
    }

    private static async Task RunDiskPartAsync(params string[] commands)
    {
        var script = Path.Combine(Path.GetTempPath(), $"shadowdisk-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(script, string.Join(Environment.NewLine, commands) + Environment.NewLine, Encoding.ASCII);
        try
        {
            using var process = Process.Start(new ProcessStartInfo("diskpart.exe", $"/s \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("Could not start diskpart.");
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("diskpart reported an error: " + LastMeaningfulLine(output));
        }
        finally
        {
            try { File.Delete(script); } catch (IOException) { }
        }
    }

    private static string LastMeaningfulLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("DISKPART>", StringComparison.Ordinal))
        ?? "no details";
}
