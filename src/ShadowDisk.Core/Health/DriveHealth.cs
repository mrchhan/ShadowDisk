using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Health;

// Stage 4: drive health. Everything here is what the DRIVE REPORTS ABOUT ITSELF (through Windows'
// MSFT_PhysicalDisk and MSFT_StorageReliabilityCounter). It is not a surface scan and never proves a
// disk is safe. Many RAID controllers and USB adapters don't pass these counters on at all.

public enum DriveMedia { Unknown, Hdd, Ssd, Scm }

/// <summary>What Windows reports about one physical drive. Null means "not reported".</summary>
public sealed record DriveHealthReport(
    int DiskNumber,
    string? Model,
    DriveMedia Media,
    ReportedHealth WindowsHealth,
    IReadOnlyList<string> OperationalStatus,
    int? TemperatureC,
    int? TemperatureMaxC,
    long? PowerOnHours,
    long? ReadErrorsTotal,
    long? ReadErrorsUncorrected,
    long? WriteErrorsTotal,
    long? WriteErrorsUncorrected,
    int? WearPercent,
    long? StartStopCycles,
    long? LoadUnloadCycles,
    string? Note = null)
{
    public bool HasCounters =>
        TemperatureC is not null || PowerOnHours is not null || ReadErrorsTotal is not null || ReadErrorsUncorrected is not null ||
        WriteErrorsTotal is not null || WriteErrorsUncorrected is not null || WearPercent is not null ||
        StartStopCycles is not null || LoadUnloadCycles is not null;
}

public enum HealthVerdict { Unknown, Good, Watch, Concern }

public sealed record HealthAssessment(HealthVerdict Verdict, string Headline, IReadOnlyList<string> Findings);

public static class HealthAssessor
{
    public const string NotAScan =
        "This is what the drive reports about itself. It is not a surface scan, and it can't prove your files are safe.";

    public static HealthAssessment Assess(DriveHealthReport r)
    {
        var concern = new List<string>();
        var watch = new List<string>();
        var good = new List<string>();
        var info = new List<string>();

        switch (r.WindowsHealth)
        {
            case ReportedHealth.Unhealthy: concern.Add("Windows reports this drive as Unhealthy. Back up its files now."); break;
            case ReportedHealth.Warning: watch.Add("Windows reports a warning for this drive."); break;
            case ReportedHealth.Healthy: good.Add("Windows reports this drive as Healthy."); break;
        }
        foreach (var s in r.OperationalStatus.Where(s => s is not ("OK" or "Online")))
            watch.Add($"Windows reports the drive's state as “{s}”.");

        if (r.ReadErrorsUncorrected > 0)
            concern.Add($"{r.ReadErrorsUncorrected:N0} read error(s) could not be corrected by the drive. Some data may already be unreadable.");
        if (r.WriteErrorsUncorrected > 0)
            concern.Add($"{r.WriteErrorsUncorrected:N0} write error(s) could not be corrected by the drive.");
        if (r.ReadErrorsUncorrected == 0 && r.WriteErrorsUncorrected == 0)
            good.Add("No uncorrected read or write errors are reported.");
        if (r.ReadErrorsTotal > 0 && r.ReadErrorsUncorrected is null or 0)
            good.Add($"{r.ReadErrorsTotal:N0} read error(s) were corrected by the drive itself. Some corrected errors are normal.");

        if (r.WearPercent is { } wear)
        {
            if (wear >= 90) concern.Add($"The drive reports {wear}% wear. It is near the end of its rated life; replace it soon.");
            else if (wear >= 70) watch.Add($"The drive reports {wear}% wear.");
            else good.Add($"Wear: {wear}% of rated life used.");
        }

        if (r.TemperatureC is { } t)
        {
            var hot = r.Media == DriveMedia.Hdd ? 55 : 70;
            if (t >= hot) watch.Add($"The drive is at {t} °C, which is warm. Check the PC's cooling.");
            else good.Add($"Temperature: {t} °C.");
        }

        if (!r.HasCounters)
            info.Add("The drive did not report detailed counters (temperature, errors, wear). This is common behind RAID " +
                      "controllers and USB adapters; it doesn't mean anything is wrong.");

        var verdict = concern.Count > 0 ? HealthVerdict.Concern
            : watch.Count > 0 ? HealthVerdict.Watch
            : r.WindowsHealth == ReportedHealth.Unknown && !r.HasCounters ? HealthVerdict.Unknown
            : HealthVerdict.Good;
        var headline = verdict switch
        {
            HealthVerdict.Concern => "Needs attention — back up this drive's files now.",
            HealthVerdict.Watch => "Worth keeping an eye on.",
            HealthVerdict.Good => "No problems reported.",
            _ => "Windows could not tell how healthy this drive is.",
        };
        return new HealthAssessment(verdict, headline, [.. concern, .. watch, .. good, .. info, NotAScan]);
    }
}
