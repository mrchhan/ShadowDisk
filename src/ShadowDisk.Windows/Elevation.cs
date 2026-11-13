using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace ShadowDisk.Windows;

[SupportedOSPlatform("windows")]
public static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts a new elevated copy of the app. Returns false if the user declined the UAC prompt.</summary>
    public static bool TryRestartElevated(string arguments = "")
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    public static void OpenDiskManagement() =>
        Process.Start(new ProcessStartInfo("diskmgmt.msc") { UseShellExecute = true });
}
