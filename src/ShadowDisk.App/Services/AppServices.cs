using System.IO;
using System.Reflection;
using System.Text.Json;
using ShadowDisk.Core.Logging;

namespace ShadowDisk.App.Services;

public static class AppServices
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadowDisk");

    public static OperationLog Log { get; } = new(Path.Combine(DataDirectory, "logs"));

    public static string Version { get; } =
        typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}

public sealed class UserSettings
{
    public string View { get; set; } = "Bar";

    private static string FilePath => Path.Combine(AppServices.DataDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new UserSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppServices.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
