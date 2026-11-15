using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ShadowDisk.Windows;

/// <summary>Whether the PC is running on mains power (documented Win32 GetSystemPowerStatus).</summary>
[SupportedOSPlatform("windows")]
public static class PowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;        // 0 = offline (battery), 1 = online (mains), 255 = unknown
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>True on mains power, false on battery, null when Windows can't tell (common on desktops).</summary>
    public static bool? IsOnMainsPower()
    {
        if (!GetSystemPowerStatus(out var status)) return null;
        return status.ACLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null,
        };
    }
}
