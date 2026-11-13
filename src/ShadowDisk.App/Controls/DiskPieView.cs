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
        var edge = new Pen(Brushes.White, 1.5);

        for (var i = 0; i < segments.Count; i++)
        {
            _slices.Add((segments[i], start, sweeps[i]));
            var selected = ReferenceEquals(segments[i].Region, SelectedRegion);
            var outer = selected ? _outer : _outer - 5;
            dc.DrawGeometry(BrushFor(segments[i].Kind), edge, Slice(_center, outer, _inner, start, sweeps[i]));
            start += sweeps[i];
        }

        var total = Text(ByteSize.Format(Layout.Disk.SizeBytes), 15, Brushes.Black, FontWeights.SemiBold);
        dc.DrawText(total, new Point(_center.X - total.Width / 2, _center.Y - total.Height + 2));
        var caption = Text($"Disk {Layout.Disk.Number}", 11, Brushes.DimGray, FontWeights.Normal);
        dc.DrawText(caption, new Point(_center.X - caption.Width / 2, _center.Y + 2));
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
