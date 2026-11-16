using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using ShadowDisk.Core.Health;
using ShadowDisk.Core.Mapping;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Windows;

/// <summary>
/// Reads what a drive reports about itself: MSFT_PhysicalDisk (health, state, media type) and its
/// MSFT_StorageReliabilityCounter (the data behind Get-StorageReliabilityCounter). Read-only.
/// Counters need administrator rights and are often missing behind RAID controllers or USB adapters.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsHealthReader
{
    public static Task<DriveHealthReport> ReadAsync(PhysicalDisk disk) => Task.Run(() => Read(disk));

    private static DriveHealthReport Read(PhysicalDisk disk)
    {
        var scope = Wmi.StorageScope();
        var physical = Wmi.All(scope, "MSFT_PhysicalDisk").FirstOrDefault(p =>
            string.Equals((p["DeviceId"] as string)?.Trim(), disk.Number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        if (physical is null)
            return Empty(disk, "Windows did not list a physical drive for this disk (common for RAID volumes and virtual disks), " +
                               "so only the disk's overall health is known.");

        var media = Wmi.ToUShort(physical["MediaType"]) switch { 3 => DriveMedia.Hdd, 4 => DriveMedia.Ssd, 5 => DriveMedia.Scm, _ => DriveMedia.Unknown };
        var health = StorageMapper.MapHealth(Wmi.ToUShort(physical["HealthStatus"]));
        var states = (physical["OperationalStatus"] as ushort[] ?? []).Select(OperationalText).ToList();

        ManagementBaseObject? counter = null;
        string? note = null;
        try
        {
            foreach (ManagementBaseObject c in physical.GetRelated("MSFT_StorageReliabilityCounter")) { counter = c; break; }
        }
        catch (ManagementException ex)
        {
            note = ex.ErrorCode == ManagementStatus.AccessDenied
                ? "Detailed counters need administrator rights. Use “Restart as administrator”."
                : "Windows could not read the drive's detailed counters.";
        }
        if (counter is null && note is null)
            note = "The drive (or its controller) did not report detailed counters.";

        return new DriveHealthReport(disk.Number, (physical["FriendlyName"] as string)?.Trim() ?? disk.FriendlyName, media, health, states,
            Positive(counter, "Temperature"), Positive(counter, "TemperatureMax"), Long(counter, "PowerOnHours"),
            Long(counter, "ReadErrorsTotal"), Long(counter, "ReadErrorsUncorrected"),
            Long(counter, "WriteErrorsTotal"), Long(counter, "WriteErrorsUncorrected"),
            Wear(counter, media), Long(counter, "StartStopCycleCount"), Long(counter, "LoadUnloadCycleCount"), note);
    }

    private static DriveHealthReport Empty(PhysicalDisk disk, string note) =>
        new(disk.Number, disk.FriendlyName, DriveMedia.Unknown, disk.Health, [], null, null, null, null, null, null, null, null, null, null, note);

    private static long? Long(ManagementBaseObject? o, string name)
    {
        if (o?[name] is not { } v) return null;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    /// <summary>
    /// Wear is an SSD measurement. Hard disks, RAID controllers and many adapters report 0 when they don't measure it,
    /// so 0 counts as "not reported" unless the drive is known to be an SSD.
    /// </summary>
    private static int? Wear(ManagementBaseObject? o, DriveMedia media) =>
        Long(o, "Wear") is { } w and >= 0 and <= 100 && (w > 0 || media == DriveMedia.Ssd) && media != DriveMedia.Hdd ? (int)w : null;

    /// <summary>Temperatures of 0 mean "not reported".</summary>
    private static int? Positive(ManagementBaseObject? o, string name) => Long(o, name) is > 0 and < 200 and var t ? (int)t : null;

    private static string OperationalText(ushort code) => code switch
    {
        2 => "OK",
        3 => "Degraded",
        5 => "Predictive failure",
        6 => "Error",
        10 => "Stopped",
        11 => "In service",
        13 => "Lost communication",
        0xD00D => "In maintenance",
        _ => $"Status {code}",
    };
}
