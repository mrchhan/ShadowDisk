using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ShadowDisk.App.Controls;
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
            StartHeroMotion();
            PlayIntro();
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnViewModelChanged;
                await vm.RefreshAsync();
                SlideInDiskList();
            }
        };
        Theme.Changed += RedrawCharts;
        Closed += (_, _) => Theme.Changed -= RedrawCharts;
    }

    /// <summary>The hero header's slow aurora, glows and shimmer. Only when Windows' "Show animations" is on.</summary>
    private void StartHeroMotion()
    {
        if (!Theme.Motion) return;
        if (TryFindResource("HeroMotion") is Storyboard motion) motion.Begin(this, true);
    }

    /// <summary>Start-up: the header slides in from both sides, then the toolbar and the disk list rise into place.</summary>
    private void PlayIntro()
    {
        Theme.SlideIn(HeroLeft, -40, 0, 650);
        Theme.SlideIn(HeroRight, 40, 150, 650);
        Theme.FadeUp(Toolbar, 250, 10);
        Theme.FadeUp(DiskListCard, 380, 24, 600);
    }

    /// <summary>The disks in the list slide in one after another.</summary>
    private void SlideInDiskList()
    {
        if (!Theme.Motion) return;
        Dispatcher.InvokeAsync(() =>
        {
            for (var i = 0; i < DiskList.Items.Count; i++)
                if (DiskList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                    Theme.SlideIn(item, -28, 80 * i, 500);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>When another disk is chosen its cards cascade into place; status messages fade in.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Theme.Motion) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedDisk):
                Dispatcher.InvokeAsync(() => Theme.Cascade(DetailsPanel, 0, 70), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.StatusText):
                StatusLine.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(400)));
                break;
            case nameof(MainViewModel.Disks):
                SlideInDiskList();
                break;
        }
    }

    /// <summary>The bar and pie paint themselves, so they are asked to repaint in the new theme's colours.</summary>
    private void RedrawCharts()
    {
        void Walk(DependencyObject node)
        {
            if (node is LayoutViewBase view) view.InvalidateVisual();
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(this);
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
