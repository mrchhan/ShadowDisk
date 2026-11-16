using System.Windows;
using System.Windows.Media;
using ShadowDisk.Core;
using ShadowDisk.Core.Layout;

namespace ShadowDisk.App.Controls;

/// <summary>
/// Donut chart of the whole disk. Each volume appears as a used slice and a free slice; system
/// partitions are grey; unallocated space is hatched. Tiny slices get a minimum angle (not to scale).
/// </summary>
public sealed class DiskPieView : LayoutViewBase
{
    private const double MinSliceDegrees = 2.5;
    private const double InnerRatio = 0.56;

    private List<(VisualSegment Segment, double Start, double Sweep)> _slices = [];
    private Point _center;
    private double _outer;
    private double _inner;

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 280 : Math.Min(availableSize.Width, 320);
        return new Size(w, Math.Min(w, 280));
    }

    protected override void OnRender(DrawingContext dc)
    {
        _slices = [];
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Layout is null) return;

        var segments = VisualSegmentBuilder.BuildPie(Layout);
        if (segments.Count == 0) return;

        _center = new Point(ActualWidth / 2, ActualHeight / 2);
        _outer = Math.Max(10, Math.Min(ActualWidth, ActualHeight) / 2 - 6);
        _inner = _outer * InnerRatio;

        var sweeps = VisualSegmentBuilder.ScaleWithMinimum(segments.Select(s => s.Bytes).ToList(), 360, MinSliceDegrees);
        var start = -90.0; // 12 o'clock
        var edge = new Pen(PanelBrush, 1.5);

        // Draw-in animation: the donut is drawn clockwise from 12 o'clock while it fades in.
        var reveal = Math.Clamp(Reveal, 0, 1);
        var animating = reveal < 1;
        var limit = -90.0 + 360.0 * reveal;
        if (animating) dc.PushOpacity(0.3 + 0.7 * reveal);

        for (var i = 0; i < segments.Count; i++)
        {
            _slices.Add((segments[i], start, sweeps[i]));
            var visible = Math.Min(sweeps[i], limit - start);
            if (visible > 0.05)
            {
                var selected = ReferenceEquals(segments[i].Region, SelectedRegion);
                var outer = selected ? _outer : _outer - 5;
                var slice = Slice(_center, outer, _inner, start, visible);
                if (selected)
                {
                    // A soft glow that breathes around the selected slice.
                    dc.PushOpacity(0.15 + 0.4 * Pulse);
                    dc.DrawGeometry(null, new Pen(SelectionBrush, 3 + 4 * Pulse), slice);
                    dc.Pop();
                }
                dc.DrawGeometry(BrushFor(segments[i].Kind), edge, slice);
            }
            start += sweeps[i];
        }

        // The AIShadow ring: electric blue to ember orange around the donut.
        if (reveal > 0.6)
        {
            dc.PushOpacity(Math.Min(1, (reveal - 0.6) / 0.4) * 0.85);
            dc.DrawEllipse(null, new Pen(AccentGradientBrush, 2.2), _center, _outer + 3, _outer + 3);
            dc.Pop();
        }

        var total = Text(ByteSize.Format(Layout.Disk.SizeBytes), 15, TextBrush, FontWeights.SemiBold);
        dc.DrawText(total, new Point(_center.X - total.Width / 2, _center.Y - total.Height + 2));
        var caption = Text($"Disk {Layout.Disk.Number}", 11, MutedBrush, FontWeights.Normal);
        dc.DrawText(caption, new Point(_center.X - caption.Width / 2, _center.Y + 2));
        if (animating) dc.Pop();
    }

    private Brush BrushFor(SegmentKind kind) => kind switch
    {
        SegmentKind.UsedData => UsedBrush,
        SegmentKind.FreeInVolume => FreeBrush,
        SegmentKind.SystemOrReserved => SystemBrush,
        _ => UnallocatedBrush,
    };

    private static Geometry Slice(Point c, double outer, double inner, double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 359.99)
        {
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(c, outer, outer));
            ring.Children.Add(new EllipseGeometry(c, inner, inner));
            ring.Freeze();
            return ring;
        }

        var large = sweepDeg > 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointAt(c, outer, startDeg), true, true);
            ctx.ArcTo(PointAt(c, outer, startDeg + sweepDeg), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(PointAt(c, inner, startDeg + sweepDeg), true, true);
            ctx.ArcTo(PointAt(c, inner, startDeg), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    private static Point PointAt(Point c, double r, double deg)
    {
        var rad = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private (VisualSegment Segment, double Start, double Sweep)? SliceAt(Point p)
    {
        var dx = p.X - _center.X;
        var dy = p.Y - _center.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < _inner || dist > _outer) return null;
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI; // -180..180, 0 = 3 o'clock
        if (angle < -90) angle += 360;                  // normalise to -90..270 to match drawing start
        foreach (var s in _slices)
            if (angle >= s.Start && angle < s.Start + s.Sweep) return s;
        return null;
    }

    protected override DiskRegion? HitTestRegion(Point p)
    {
        return SliceAt(p)?.Segment.Region;
    }

    protected override string? DescribeAt(Point p)
    {
        if (SliceAt(p) is not { } s) return null;
        var share = Layout is { Disk.SizeBytes: > 0 } l ? (double)s.Segment.Bytes / l.Disk.SizeBytes : 0;
        return $"{s.Segment.Label}: {ByteSize.Format(s.Segment.Bytes)} ({share:P1} of disk)";
    }
}
