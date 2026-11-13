using System.Windows;
using ShadowDisk.App.ViewModels;

namespace ShadowDisk.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm) await vm.RefreshAsync();
        };
    }

    /// <summary>
    /// Keeps the whole window, including its title bar, on screen. On small screens
    /// (e.g. 1366×768 laptops) the preferred 1240×820 size would push the title bar off the top.
    /// </summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea; // screen minus the taskbar
        const double margin = 16;

        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width - margin);
        Height = Math.Min(Height, area.Height - margin);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);

        // On very small screens just fill the available space.
        if (area.Height < 700 || area.Width < 1000) WindowState = WindowState.Maximized;
    }
}
