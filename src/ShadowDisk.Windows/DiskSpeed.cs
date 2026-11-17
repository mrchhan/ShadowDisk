using System.Diagnostics;
using System.Runtime.Versioning;
using ShadowDisk.Core;

namespace ShadowDisk.Windows;

/// <summary>A quick, read-only speed test used for time estimates. Never writes.</summary>
[SupportedOSPlatform("windows")]
public static class DiskSpeed
{
    /// <summary>Reads up to 128 MiB from <paramref name="offset"/> and returns MiB/s, or null if the disk could not be read.</summary>
    public static Task<double?> MeasureReadAsync(int diskNumber, long diskSize, int sectorSize, long offset, long length, CancellationToken ct) =>
        Task.Run<double?>(() =>
        {
            const int Block = 8 * 1024 * 1024;
            length = Math.Min(Math.Min(length, 128L * ByteSize.MiB), diskSize - offset);
            if (length < Block) return null;
            try
            {
                using var reader = new PhysicalDriveReader(diskNumber, diskSize, sectorSize, Block);
                var clock = Stopwatch.StartNew();
                long done = 0;
                while (done + Block <= length && !ct.IsCancellationRequested)
                {
                    if (reader.Read(offset + done, Block) is not null) return null;
                    done += Block;
                }
                var seconds = clock.Elapsed.TotalSeconds;
                return done == 0 || seconds <= 0 ? null : done / (double)ByteSize.MiB / seconds;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }, ct);
}
