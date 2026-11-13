using System.Globalization;

namespace ShadowDisk.Core;

/// <summary>
/// All sizes are kept as exact byte counts (long). Rounding only happens when text is shown,
/// and the unit is always explicit: GiB = 1024^3 bytes (what Windows Explorer calls "GB"),
/// GB = 1000^3 bytes (what drive makers print on the box).
/// </summary>
public static class ByteSize
{
    public const long KiB = 1024L;
    public const long MiB = 1024L * KiB;
    public const long GiB = 1024L * MiB;
    public const long TiB = 1024L * GiB;

    private static readonly string[] BinaryUnits = ["bytes", "KiB", "MiB", "GiB", "TiB", "PiB"];
    private static readonly string[] DecimalUnits = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, bool binary = true, int decimals = 2)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes), "Size cannot be negative.");
        var step = binary ? 1024.0 : 1000.0;
        var units = binary ? BinaryUnits : DecimalUnits;
        if (bytes < step) return string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");

        double value = bytes;
        var unit = 0;
        while (value >= step && unit < units.Length - 1)
        {
            value /= step;
            unit++;
        }
        // Truncate rather than round up, so we never display more space than really exists.
        var factor = Math.Pow(10, decimals);
        var truncated = Math.Floor(value * factor) / factor;
        return truncated.ToString("F" + decimals, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>"465.76 GiB (500,107,862,016 bytes)"</summary>
    public static string FormatWithExact(long bytes) =>
        $"{Format(bytes)} ({bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)";

    public static bool IsAligned(long offsetBytes, long alignmentBytes) =>
        alignmentBytes > 0 && offsetBytes % alignmentBytes == 0;
}
