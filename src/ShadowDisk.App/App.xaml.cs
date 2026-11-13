using System.Windows;
using System.Windows.Threading;

namespace ShadowDisk.App;

public partial class App : Application
{
    public static bool StartInDemoMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartInDemoMode = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.AppServices.Log.Error($"Unexpected error: {e.Exception.GetType().Name}: {e.Exception.Message}");
        MessageBox.Show(
            "Something went wrong inside ShadowDisk. No changes were made to your disks (this version is read-only).\n\n" +
            e.Exception.Message,
            "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
