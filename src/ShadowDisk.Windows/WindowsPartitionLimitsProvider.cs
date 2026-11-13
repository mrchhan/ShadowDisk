using System.Management;
using System.Runtime.Versioning;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.Windows;

/// <summary>
/// Asks Windows how far a partition can shrink or grow, using MSFT_Partition.GetSupportedSize
/// (the API behind PowerShell's Get-PartitionSupportedSize). GetSupportedSize only READS: it
/// analyses the volume and reports a minimum and maximum. It never resizes anything.
/// If Windows can't answer (no admin rights, unsupported volume, timeout), ShadowDisk falls back
/// to its own cautious estimate and says so clearly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPartitionLimitsProvider : IPartitionLimitsProvider
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public async Task<SizeLimits> GetLimitsAsync(DiskLayout layout, Partition partition, PlanKind kind, CancellationToken cancellationToken)
    {
        if (kind == PlanKind.CreateVolume) throw new ArgumentOutOfRangeException(nameof(kind));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var (min, max) = await Task.Run(() => Query(partition.DiskNumber, partition.PartitionNumber), timeout.Token)
                .WaitAsync(timeout.Token);
            return PlanLimits.FromWindows(layout, partition, kind, min, max);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PlanLimits.Estimate(layout, partition, kind,
                "Windows took too long to report the limits, so these are ShadowDisk's cautious estimates.");
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return PlanLimits.Estimate(layout, partition, kind,
                "Estimated: Windows needs administrator rights to report exact limits. Use “Restart as administrator”.");
        }
        catch (UnauthorizedAccessException)
        {
            return PlanLimits.Estimate(layout, partition, kind,
                "Estimated: Windows needs administrator rights to report exact limits. Use “Restart as administrator”.");
        }
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException or OverflowException
                                       or System.Runtime.InteropServices.COMException)
        {
            return PlanLimits.Estimate(layout, partition, kind,
                $"Estimated: Windows could not report exact limits ({ex.Message.Trim()}).");
        }
    }

    private static (long Min, long Max) Query(int diskNumber, int partitionNumber)
    {
        var scope = new ManagementScope(StorageNamespace);
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
            $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}"));
        using var results = searcher.Get();
        foreach (ManagementObject partition in results)
        {
            using (partition)
            {
                using var output = partition.InvokeMethod("GetSupportedSize", null, null)
                    ?? throw new InvalidOperationException("Windows returned no result.");
                var status = Convert.ToUInt32(output["ReturnValue"] ?? 1u);
                if (status != 0)
                    throw new InvalidOperationException($"Windows reported status {status}");
                var min = Convert.ToUInt64(output["SizeMin"] ?? 0UL);
                var max = Convert.ToUInt64(output["SizeMax"] ?? 0UL);
                return (checked((long)min), checked((long)max));
            }
        }
        throw new InvalidOperationException("The partition was not found. Refresh and try again.");
    }
}
