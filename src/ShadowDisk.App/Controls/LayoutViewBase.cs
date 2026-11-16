using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ShadowDisk.Core;
using ShadowDisk.Core.Layout;

namespace ShadowDisk.App.Controls;

/// <summary>
/// Shared plumbing for the bar and pie views. These controls only DISPLAY a layout and let the
/// user select a region; clicking or dragging never changes sizes or touches a disk.
/// </summary>
public abstract class LayoutViewBase : FrameworkElement
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(DiskLayout), typeof(LayoutViewBase),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutChanged));

    public static readonly DependencyProperty SelectedRegionProperty = DependencyProperty.Register(
        nameof(SelectedRegion), typeof(DiskRegion), typeof(LayoutViewBase),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>0 → 1 while a newly chosen disk's chart sweeps in. Drawing only; 1 means fully shown.</summary>
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(LayoutViewBase), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Reveal
    {
        get => (double)GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    /// <summary>0 ↔ 1, breathing, for the glow around the selected block or slice. Drawing only.</summary>
    public static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        nameof(Pulse), typeof(double), typeof(LayoutViewBase), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Pulse
    {
        get => (double)GetValue(PulseProperty);
        set => SetValue(PulseProperty, value);
    }

    /// <summary>The used part of each volume grows during the second part of the sweep-in.</summary>
    protected double UsedGrow => Math.Clamp((Reveal - 0.35) / 0.65, 0, 1);

    public static readonly DependencyProperty IsSelectableProperty = DependencyProperty.Register(
        nameof(IsSelectable), typeof(bool), typeof(LayoutViewBase), new PropertyMetadata(true));

    /// <summary>When false, the view only displays the layout (used for the preview bars).</summary>
    public bool IsSelectable
    {
        get => (bool)GetValue(IsSelectableProperty);
        set => SetValue(IsSelectableProperty, value);
    }

    private readonly ToolTip _toolTip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    protected LayoutViewBase()
    {
        _toolTip.PlacementTarget = this;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        Loaded += (_, _) =>
        {
            if (!IsSelectable || !Theme.Motion) return;
            var breathe = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1100))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(breathe, 30);   // gentle on the CPU
            BeginAnimation(PulseProperty, breathe);
        };
        Unloaded += (_, _) => BeginAnimation(PulseProperty, null);
    }

    public DiskLayout? Layout
    {
        get => (DiskLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public DiskRegion? SelectedRegion
    {
        get => (DiskRegion?)GetValue(SelectedRegionProperty);
        set => SetValue(SelectedRegionProperty, value);
    }

    protected Brush UsedBrush => Find("UsedBrush", Brushes.RoyalBlue);
    protected Brush FreeBrush => Find("FreeBrush", Brushes.LightBlue);
    protected Brush SystemBrush => Find("SystemBrush", Brushes.Gray);
    protected Brush UnallocatedBrush => Find("UnallocatedBrush", Brushes.White);
    protected Brush SelectionBrush => Find("SelectionBrush", Brushes.Orange);
    protected Brush TextBrush => Find("TextBrush", Brushes.Black);
    protected Brush MutedBrush => Find("MutedText", Brushes.DimGray);
    protected Brush PanelBrush => Find("PanelBrush", Brushes.White);
    protected Brush LineBrush => Find("LineBrush", Brushes.Gray);
    protected Brush AccentGradientBrush => Find("AccentGradient", Brushes.SteelBlue);

    /// <summary>Returns the region under a point, or null.</summary>
    protected abstract DiskRegion? HitTestRegion(Point p);

    /// <summary>Tooltip text for the point (may be more specific than the region, e.g. used vs free).</summary>
    protected abstract string? DescribeAt(Point p);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsSelectable) return;
        Focus();
        if (HitTestRegion(e.GetPosition(this)) is { } region) SelectedRegion = region;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var text = DescribeAt(e.GetPosition(this));
        _toolTip.Content = text;
        _toolTip.IsOpen = text is not null && IsMouseOver;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _toolTip.IsOpen = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Layout is null || !IsSelectable) return;
        var regions = Layout.Regions.Where(r => r.Kind != RegionKind.Gap).ToList();
        if (regions.Count == 0) return;
        var index = SelectedRegion is null ? -1 : regions.FindIndex(r => ReferenceEquals(r, SelectedRegion));
        if (e.Key is Key.Right or Key.Down) { SelectedRegion = regions[Math.Min(regions.Count - 1, index + 1)]; e.Handled = true; }
        if (e.Key is Key.Left or Key.Up) { SelectedRegion = regions[Math.Max(0, index - 1)]; e.Handled = true; }
    }

    protected static string RegionText(DiskRegion r)
    {
        if (r.Partition is not { } p) return $"Unallocated: {ByteSize.Format(r.SizeBytes)} (not part of any partition)";
        var text = $"{p.DisplayName}: {ByteSize.Format(r.SizeBytes)}";
        if (p.Volume is { } v)
            text += $"\n{v.FileSystem ?? "Unknown file system"} · {ByteSize.Format(v.UsedBytes)} used · {ByteSize.Format(v.FreeBytes)} free";
        return text;
    }

    protected FormattedText Text(string s, double size, Brush brush, FontWeight weight) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LayoutViewBase view && e.NewValue is DiskLayout layout)
        {
            AutomationProperties.SetName(view, VisualSegmentBuilder.Describe(layout));
            // Sweep in when a different disk is shown (not on every preview change of the same disk).
            var old = e.OldValue as DiskLayout;
            if (view.IsSelectable && Theme.Motion && (old is null || old.Disk.Number != layout.Disk.Number || old.Disk.UniqueId != layout.Disk.UniqueId))
                view.BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(950))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }
}
