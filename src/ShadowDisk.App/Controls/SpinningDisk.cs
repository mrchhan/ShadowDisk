using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ShadowDisk.App.Controls;

/// <summary>
/// Counts work that is reading or changing disks (scans, copies, wizard steps), so every
/// <see cref="SpinningDisk"/> with <see cref="SpinningDisk.FollowsActivity"/> spins up while it runs.
/// UI thread only.
/// </summary>
public static class DiskActivity
{
    private static int _count;

    public static bool IsActive => _count > 0;
    public static event Action? Changed;

    /// <summary>Marks work as running until the returned object is disposed.</summary>
    public static IDisposable Begin()
    {
        _count++;
        Changed?.Invoke();
        return new Token();
    }

    private sealed class Token : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _count = Math.Max(0, _count - 1);
            Changed?.Invoke();
        }
    }
}

/// <summary>
/// A drawn hard-disk platter: tracks, blue and orange light reflections, a spindle hub, a read/write arm
/// and an activity light. Idle, it turns slowly; busy, it spins fast, the arm seeks across the tracks and
/// the light blinks orange. Purely decorative: it shows activity, it never measures anything.
/// Still when Windows' "Show animations" setting is off.
/// </summary>
public sealed class SpinningDisk : FrameworkElement
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(SpinningDisk), new PropertyMetadata(false, (d, _) => ((SpinningDisk)d).UpdateMotion()));

    public static readonly DependencyProperty IsSpinningProperty = DependencyProperty.Register(
        nameof(IsSpinning), typeof(bool), typeof(SpinningDisk), new PropertyMetadata(true, (d, _) => ((SpinningDisk)d).UpdateMotion()));

    public static readonly DependencyProperty FollowsActivityProperty = DependencyProperty.Register(
        nameof(FollowsActivity), typeof(bool), typeof(SpinningDisk), new PropertyMetadata(false, (d, _) => ((SpinningDisk)d).UpdateMotion()));

    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(SpinningDisk), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArmProperty = DependencyProperty.Register(
        nameof(Arm), typeof(double), typeof(SpinningDisk), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LightProperty = DependencyProperty.Register(
        nameof(Light), typeof(double), typeof(SpinningDisk), new FrameworkPropertyMetadata(0.4, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Spin fast with the arm seeking (something is working).</summary>
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

    /// <summary>Turn slowly while idle. When false the platter rests until busy.</summary>
    public bool IsSpinning { get => (bool)GetValue(IsSpinningProperty); set => SetValue(IsSpinningProperty, value); }

    /// <summary>Also spin fast whenever <see cref="DiskActivity"/> reports work anywhere in the app.</summary>
    public bool FollowsActivity { get => (bool)GetValue(FollowsActivityProperty); set => SetValue(FollowsActivityProperty, value); }

    /// <summary>Platter rotation in degrees (animated).</summary>
    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }

    /// <summary>Arm position: 0 = parked at the outer edge, 1 = near the hub (animated).</summary>
    public double Arm { get => (double)GetValue(ArmProperty); set => SetValue(ArmProperty, value); }

    /// <summary>Activity light brightness 0..1 (animated).</summary>
    public double Light { get => (double)GetValue(LightProperty); set => SetValue(LightProperty, value); }

    private bool _wasBusy;
    private bool _wasSpinning;

    public SpinningDisk()
    {
        AutomationProperties.SetName(this, "Disk activity");
        Loaded += (_, _) =>
        {
            DiskActivity.Changed += UpdateMotion;
            Theme.Changed += InvalidateVisual;
            _wasBusy = !Busy;   // force the first update
            UpdateMotion();
        };
        Unloaded += (_, _) =>
        {
            DiskActivity.Changed -= UpdateMotion;
            Theme.Changed -= InvalidateVisual;
            BeginAnimation(AngleProperty, null);
            BeginAnimation(ArmProperty, null);
            BeginAnimation(LightProperty, null);
        };
    }

    private bool Busy => IsBusy || (FollowsActivity && DiskActivity.IsActive);

    private void UpdateMotion()
    {
        if (!IsLoaded) return;
        var busy = Busy;
        var spinning = busy || IsSpinning;
        if (busy == _wasBusy && spinning == _wasSpinning) return;
        _wasBusy = busy;
        _wasSpinning = spinning;
        InvalidateVisual();
        if (!Theme.Motion) return;

        // Platter: carry on from the current angle at the new speed (0.45 s per turn busy, 3.5 s idle).
        var from = Angle % 360;
        if (spinning)
        {
            var spin = new DoubleAnimation(from, from + 360, TimeSpan.FromSeconds(busy ? 0.45 : 3.5)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, busy ? 60 : 30);
            BeginAnimation(AngleProperty, spin);
        }
        else
        {
            // Coast to a stop.
            BeginAnimation(AngleProperty, new DoubleAnimation(from, from + 40, TimeSpan.FromMilliseconds(900))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }

        if (busy)
        {
            // The arm seeks between tracks like a real drive reading.
            var seek = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2.4) };
            double[] stops = [0.15, 0.85, 0.35, 0.95, 0.55, 0.2, 0.7, 0.15];
            for (var i = 0; i < stops.Length; i++)
                seek.KeyFrames.Add(new EasingDoubleKeyFrame(stops[i], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.3 * i)),
                    new CubicEase { EasingMode = EasingMode.EaseInOut }));
            BeginAnimation(ArmProperty, seek);
            BeginAnimation(LightProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(140)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            BeginAnimation(ArmProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(LightProperty, new DoubleAnimation(0.25, 0.6, TimeSpan.FromSeconds(1.6))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 32 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 32 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8) return;
        var r = size / 2 - 1;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var dark = Theme.IsDark;
        var busy = _wasBusy;

        // Platter: brushed metal with a blue tint, and a fine rim in the accent colour.
        var platter = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            GradientStops =
            {
                new GradientStop(dark ? Color.FromRgb(0xE4, 0xEC, 0xF8) : Color.FromRgb(0xF4, 0xF7, 0xFC), 0),
                new GradientStop(dark ? Color.FromRgb(0x8C, 0x9B, 0xB8) : Color.FromRgb(0xB7, 0xC3, 0xD6), 0.65),
                new GradientStop(dark ? Color.FromRgb(0x4A, 0x58, 0x76) : Color.FromRgb(0x7D, 0x8B, 0xA4), 1),
            },
        };
        dc.DrawEllipse(platter, new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x3B, 0xA4, 0xFF)), Math.Max(1, r * 0.06)), c, r, r);

        // Tracks.
        var track = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x10, 0x20, 0x40)), Math.Max(0.5, r * 0.025));
        foreach (var t in new[] { 0.86, 0.72, 0.58, 0.44 }) dc.DrawEllipse(null, track, c, r * t, r * t);

        // Rotating light reflections (electric blue and ember orange) and a marker: this is what shows the spin.
        dc.PushTransform(new RotateTransform(Angle, c.X, c.Y));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(busy ? (byte)0x88 : (byte)0x60, 0x3B, 0xA4, 0xFF)), null, Wedge(c, r * 0.22, r * 0.95, -20, 40));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(busy ? (byte)0x80 : (byte)0x55, 0xFF, 0x8A, 0x2A)), null, Wedge(c, r * 0.22, r * 0.95, 160, 40));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), null, Wedge(c, r * 0.22, r * 0.95, 70, 12));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), null, new Point(c.X, c.Y - r * 0.78), r * 0.05, r * 0.05);
        dc.Pop();

        // Spindle hub.
        dc.DrawEllipse(new SolidColorBrush(dark ? Color.FromRgb(0x2A, 0x36, 0x52) : Color.FromRgb(0x5B, 0x68, 0x80)), null, c, r * 0.2, r * 0.2);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xD5, 0xDD, 0xEA)), null, c, r * 0.08, r * 0.08);

        // Read/write arm from the top-right pivot; 0 = parked outside, 1 = near the hub.
        var pivot = new Point(c.X + r * 0.85, c.Y - r * 0.85);
        var angle = (95 + 32 * Math.Clamp(Arm, 0, 1)) * Math.PI / 180;
        var head = new Point(pivot.X + r * Math.Cos(angle), pivot.Y + r * Math.Sin(angle));
        var armPen = new Pen(new SolidColorBrush(dark ? Color.FromRgb(0xF1, 0xF5, 0xFB) : Color.FromRgb(0x34, 0x3F, 0x55)), Math.Max(1.2, r * 0.1))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), armPen.Thickness + 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            pivot, head);
        dc.DrawLine(armPen, pivot, head);
        dc.DrawEllipse(armPen.Brush, null, head, r * 0.08, r * 0.08);
        dc.DrawEllipse(new SolidColorBrush(dark ? Color.FromRgb(0x5D, 0x6B, 0x88) : Color.FromRgb(0x4B, 0x55, 0x63)), null, pivot, r * 0.15, r * 0.15);

        // Activity light on the pivot: orange and blinking while busy, soft blue while idle.
        var lightColor = busy ? Color.FromRgb(0xFF, 0x8A, 0x2A) : Color.FromRgb(0x3B, 0xA4, 0xFF);
        dc.PushOpacity(Math.Clamp(Light, 0, 1));
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x60, lightColor.R, lightColor.G, lightColor.B)), null, pivot, r * 0.17, r * 0.17);
        dc.DrawEllipse(new SolidColorBrush(lightColor), null, pivot, r * 0.08, r * 0.08);
        dc.Pop();
    }

    /// <summary>A ring segment between two radii (degrees, 0 = 3 o'clock, clockwise).</summary>
    private static Geometry Wedge(Point c, double inner, double outer, double startDeg, double sweepDeg)
    {
        static Point At(Point c, double radius, double deg)
        {
            var a = deg * Math.PI / 180;
            return new Point(c.X + radius * Math.Cos(a), c.Y + radius * Math.Sin(a));
        }

        var figure = new PathFigure { StartPoint = At(c, inner, startDeg), IsClosed = true };
        figure.Segments.Add(new LineSegment(At(c, outer, startDeg), false));
        figure.Segments.Add(new ArcSegment(At(c, outer, startDeg + sweepDeg), new Size(outer, outer), 0, sweepDeg > 180, SweepDirection.Clockwise, false));
        figure.Segments.Add(new LineSegment(At(c, inner, startDeg + sweepDeg), false));
        figure.Segments.Add(new ArcSegment(At(c, inner, startDeg), new Size(inner, inner), 0, sweepDeg > 180, SweepDirection.Counterclockwise, false));
        var geometry = new PathGeometry { Figures = { figure } };
        geometry.Freeze();
        return geometry;
    }
}
