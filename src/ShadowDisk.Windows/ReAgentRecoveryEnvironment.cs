using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ShadowDisk.Core.Operations;
using ShadowDisk.Core.Recovery;

namespace ShadowDisk.Windows;

/// <summary>
/// Windows Recovery Environment through Microsoft's own tool, reagentc.exe:
/// <c>/info</c> to read where it is, <c>/disable</c> and <c>/enable</c> to turn it off and on (Microsoft KB5028997).
/// Each command runs once. Needs administrator rights.
///
/// The location is read from the "\\?\GLOBALROOT\device\harddiskN\partitionM" path that reagentc prints,
/// which is the same in every Windows language. No location means WinRE is off.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ReAgentRecoveryEnvironment : IRecoveryEnvironment
{
    private static string Tool => Path.Combine(Environment.SystemDirectory, "reagentc.exe");

    /// <summary>Where reagentc /disable keeps the recovery image (C:\Windows\System32\Recovery\Winre.wim).</summary>
    public static string ImagePath => Path.Combine(Environment.SystemDirectory, "Recovery", "Winre.wim");

    public bool IsSimulated => false;

    public async Task<RecoveryStatus> GetStatusAsync(CancellationToken ct)
    {
        var (code, output) = await RunAsync("/info", ct);
        if (code != 0)
            throw new InvalidOperationException($"reagentc /info failed (exit code {code}): {Short(output)}");
        var match = LocationPattern().Match(output);
        var onWindowsDrive = File.Exists(ImagePath);
        var details = Short(output) + (onWindowsDrive ? $" | Image found at {ImagePath}." : "");
        return match.Success
            ? new RecoveryStatus(true, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), onWindowsDrive, details)
            : new RecoveryStatus(false, null, null, onWindowsDrive, details);
    }

    public Task<BackendResult> DisableAsync(CancellationToken ct) => RunOnceAsync("/disable", ct);

    public Task<BackendResult> EnableAsync(CancellationToken ct) => RunOnceAsync("/enable", ct);

    private static async Task<BackendResult> RunOnceAsync(string argument, CancellationToken ct)
    {
        if (!Elevation.IsElevated()) return new BackendResult(false, "Administrator rights are needed.");
        var (code, output) = await RunAsync(argument, ct);
        return code == 0 ? new BackendResult(true, null) : new BackendResult(false, $"reagentc {argument} failed (exit code {code}): {Short(output)}");
    }

    private static async Task<(int Code, string Output)> RunAsync(string argument, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(Tool, argument)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start reagentc.");
        var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);   // never abandon reagentc half-way
        ct.ThrowIfCancellationRequested();
        return (process.ExitCode, output + error);
    }

    private static string Short(string text)
    {
        var t = string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return t.Length > 400 ? t[..400] + "…" : t;
    }

    [GeneratedRegex(@"harddisk(\d+)\\partition(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LocationPattern();
}
