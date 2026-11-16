using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace ShadowDisk.Windows;

/// <summary>Runs one diskpart script, once. Throws with diskpart's last message when it reports an error.</summary>
[SupportedOSPlatform("windows")]
internal static class DiskPart
{
    public static async Task RunAsync(params string[] commands)
    {
        await EnsureVirtualDiskServiceAsync();
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

    /// <summary>
    /// diskpart needs the Virtual Disk Service (vds). Windows starts it on demand and stops it when idle; on a slow
    /// disk it can take longer to start than diskpart waits, and diskpart then fails with "error starting the disk
    /// management services" (nothing is changed). So start it first and wait: "net start vds" returns once the
    /// service is running (or reports that it already is). This changes no disk. Any problem here is ignored;
    /// diskpart then reports its own error.
    /// </summary>
    private static async Task EnsureVirtualDiskServiceAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("net.exe", "start vds")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return;
            _ = await process.StandardOutput.ReadToEndAsync();
            _ = await process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string LastMeaningfulLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("DISKPART>", StringComparison.Ordinal))
        ?? "no details";
}
