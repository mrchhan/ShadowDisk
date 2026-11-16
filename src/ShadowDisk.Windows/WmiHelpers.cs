using System.Globalization;
using System.Management;
using System.Runtime.Versioning;

namespace ShadowDisk.Windows;

[SupportedOSPlatform("windows")]
internal static class Wmi
{
    public const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    public static ManagementScope StorageScope()
    {
        var scope = new ManagementScope(StorageNamespace);
        scope.Connect();
        return scope;
    }

    /// <summary>Returns every instance of a class. Filtering is done in C# to avoid WQL escaping of paths.</summary>
    public static List<ManagementObject> All(ManagementScope scope, string className)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {className}"));
        using var results = searcher.Get();
        var list = new List<ManagementObject>();
        foreach (ManagementObject o in results) list.Add(o);
        return list;
    }

    public static ManagementObject? Disk(ManagementScope scope, int number) =>
        All(scope, "MSFT_Disk").FirstOrDefault(d => ToInt(d["Number"]) == number);

    public static ManagementObject? Partition(ManagementScope scope, int disk, int partition) =>
        All(scope, "MSFT_Partition").FirstOrDefault(p => ToInt(p["DiskNumber"]) == disk && ToInt(p["PartitionNumber"]) == partition);

    /// <summary>Calls a CIM method once. Returns the Windows status code (0 = success) and the output.</summary>
    public static (uint Code, ManagementBaseObject? Output) Invoke(ManagementObject target, string method, Action<ManagementBaseObject>? setParams = null)
    {
        ManagementBaseObject? inParams = null;
        if (setParams is not null)
        {
            inParams = target.GetMethodParameters(method);
            setParams(inParams);
        }
        var output = target.InvokeMethod(method, inParams, null);
        var code = output?["ReturnValue"] is { } rv ? Convert.ToUInt32(rv, CultureInfo.InvariantCulture) : 1u;
        return (code, output);
    }

    /// <summary>
    /// Asks the Storage service to re-read every disk (MSFT_StorageSetting.UpdateHostStorageCache, the API behind
    /// Update-HostStorageCache). Changes made with diskpart are not always visible to the Storage API until then.
    /// Read-only for the disks; failures are ignored.
    /// </summary>
    public static void RefreshStorageCache()
    {
        try
        {
            var scope = StorageScope();
            using var setting = new ManagementClass(scope, new ManagementPath("MSFT_StorageSetting"), null);
            setting.InvokeMethod("UpdateHostStorageCache", null, null);
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    public static int ToInt(object? value) => value is null ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    public static ushort ToUShort(object? value) => value is null ? (ushort)0 : Convert.ToUInt16(value, CultureInfo.InvariantCulture);
    public static bool ToBool(object? value) => value is bool b && b;

    /// <summary>Plain-language meaning of common Storage Management status codes.</summary>
    public static string Explain(uint code) => code switch
    {
        0 => "Success",
        1 => "Not supported",
        40001 => "Access denied (run ShadowDisk as administrator)",
        40002 => "There are not enough resources to complete the operation",
        41000 => "The disk is offline",
        42002 => "The requested size is not supported",
        42007 => "The extent specified is not valid or is already in use",
        _ => $"Windows status code {code}",
    };
}
