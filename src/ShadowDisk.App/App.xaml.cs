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
        Theme.Load();
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.AppServices.Log.Error($"Unexpected error: {e.Exception.GetType().Name}: {e.Exception.Message}");
        MessageBox.Show(
            "Something went wrong inside ShadowDisk. If a disk change was in progress, check the activity log and click Refresh to see the disk as it is now before doing anything else.\n\n" +
            e.Exception.Message,
            "ShadowDisk", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
