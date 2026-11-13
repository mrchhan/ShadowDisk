using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ShadowDisk.Core;
using ShadowDisk.Core.Layout;

namespace ShadowDisk.App.Controls;

/// <summary>
/// Horizontal bar in on-disk order. Each partition is a block; inside a volume the dark part is
/// used data and the light part is free space. Hatched blocks are unallocated disk space.
/// Very small partitions are drawn at a minimum width so they stay visible (not to scale).
///
/// Plan previews can also show:
/// • a highlighted byte range (the space a plan changes), drawn as a dashed orange box;
/// • a drag handle at a byte offset. Dragging it only reports a new offset through
///   <see cref="HandleDragCommand"/>; the plan decides what to do with it. It never touches a disk.
/// </summary>
public sealed class DiskBarView : LayoutViewBase
{
    private const double BarHeight = 54;
    private const double LabelHeight = 40;
    private const double MinBlockWidth = 14;
    private const double HandleGrab = 8;

    public static readonly DependencyProperty HighlightStartProperty = DependencyProperty.Register(
        nameof(HighlightStart), typeof(long), typeof(DiskBarView), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightLengthProperty = DependencyProperty.Register(
        nameof(HighlightLength), typeof(long), typeof(DiskBarView), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HandleOffsetProperty = DependencyProperty.Register(
        nameof(HandleOffset), typeof(long), typeof(DiskBarView), new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HandleDragCommandProperty = DependencyProperty.Register(
        nameof(HandleDragCommand), typeof(ICommand), typeof(DiskBarView), new PropertyMetadata(null));

    private List<(BarBlock Block, Rect Rect)> _blocks = [];
    private bool _dragging;

    /// <summary>Start (bytes) of the area to outline. Ignored when <see cref="HighlightLength"/> is 0.</summary>
    public long HighlightStart
    {
        get => (long)GetValue(HighlightStartProperty);
        set => SetValue(HighlightStartProperty, value);
    }

    public long HighlightLength
    {
        get => (long)GetValue(HighlightLengthProperty);
        set => SetValue(HighlightLengthProperty, value);
    }

    /// <summary>Byte offset where a drag handle is drawn, or -1 for none.</summary>
    public long HandleOffset
    {
        get => (long)GetValue(HandleOffsetProperty);
        set => SetValue(HandleOffsetProperty, value);
    }

    /// <summary>Executed with the new byte offset (long) while the handle is dragged.</summary>
    public ICommand? HandleDragCommand
    {
        get => (ICommand?)GetValue(HandleDragCommandProperty);
        set => SetValue(HandleDragCommandProperty, value);
    }

    private bool HasHandle => HandleOffset >= 0 && HandleDragCommand is not null && _blocks.Count > 0;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, BarHeight + LabelHeight + 6);

    protected override void OnRender(DrawingContext dc)
    {
        _blocks = [];
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // makes the whole area clickable
        if (Layout is null) return;

        var blocks = VisualSegmentBuilder.BuildBar(Layout);
        if (blocks.Count == 0) return;

        var width = Math.Max(1, ActualWidth - 2);
        var widths = VisualSegmentBuilder.ScaleWithMinimum(blocks.Select(b => b.Region.SizeBytes).ToList(), width, MinBlockWidth);
        var divider = new Pen(Brushes.White, 2);
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), 1);
        var x = 1.0;

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var rect = new Rect(x, 1, widths[i], BarHeight);
            _blocks.Add((b, rect));

            if (b.Region.Partition is null)
            {
                dc.DrawRectangle(UnallocatedBrush, null, rect);
            }
            else if (b.Region.Partition.Volume is not null)
            {
                var usedWidth = rect.Width * b.UsedFraction;
                dc.DrawRectangle(FreeBrush, null, rect);
                dc.DrawRectangle(UsedBrush, null, new Rect(rect.X, rect.Y, usedWidth, rect.Height));
            }
            else
            {
                dc.DrawRectangle(SystemBrush, null, rect);
            }
            dc.DrawLine(divider, rect.TopRight, rect.BottomRight);

            if (rect.Width > 46)
            {
                var name = Text(b.Label, 12, Brushes.Black, FontWeights.SemiBold);
                name.MaxTextWidth = rect.Width - 6;
                name.MaxTextHeight = 18;
                name.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(name, new Point(rect.X + 3, BarHeight + 5));

                var size = Text(ByteSize.Format(b.Region.SizeBytes), 11, Brushes.DimGray, FontWeights.Normal);
                size.MaxTextWidth = rect.Width - 6;
                size.MaxTextHeight = 16;
                size.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(size, new Point(rect.X + 3, BarHeight + 22));
            }
            x += widths[i];
        }

        dc.DrawRectangle(null, outline, new Rect(1, 1, width, BarHeight));

        if (HighlightLength > 0)
        {
            // Only the space that changes: dashed orange box with a light orange wash.
            var x1 = XFor(HighlightStart);
            var x2 = Math.Max(x1 + 3, XFor(HighlightStart + HighlightLength));
            var area = new Rect(x1, 1, x2 - x1, BarHeight);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0xF5, 0x9E, 0x0B)), null, area);
            var pen = new Pen(SelectionBrush, 2.5) { DashStyle = new DashStyle([3, 2], 0) };
            dc.DrawRectangle(null, pen, Rect.Inflate(area, -1.25, -1.25));
        }
        else
        {
            var selected = _blocks.FirstOrDefault(t => ReferenceEquals(t.Block.Region, SelectedRegion));
            if (selected.Block is not null)
                dc.DrawRectangle(null, new Pen(SelectionBrush, 3), Rect.Inflate(selected.Rect, -1.5, -1.5));
        }

        if (HasHandle)
        {
            var hx = XFor(HandleOffset);
            var handle = new Rect(hx - 4, -2, 8, BarHeight + 6);
            dc.DrawRoundedRectangle(SelectionBrush, new Pen(Brushes.White, 1.5), handle, 3, 3);
            var grip = new Pen(Brushes.White, 1);
            for (var i = -1; i <= 1; i++)
                dc.DrawLine(grip, new Point(hx - 2, BarHeight / 2 + i * 4), new Point(hx + 2, BarHeight / 2 + i * 4));
        }
    }

    /// <summary>Horizontal position for a byte offset, following the same scaling as the blocks.</summary>
    private double XFor(long offset)
    {
        foreach (var (block, rect) in _blocks)
        {
            var r = block.Region;
            if (offset < r.OffsetBytes) return rect.X;
            if (offset <= r.EndBytes)
                return rect.X + (r.SizeBytes == 0 ? 0 : (offset - r.OffsetBytes) / (double)r.SizeBytes * rect.Width);
        }
        return _blocks.Count == 0 ? 0 : _blocks[^1].Rect.Right;
    }

    /// <summary>Byte offset for a horizontal position (the inverse of <see cref="XFor"/>).</summary>
    private long OffsetFor(double x)
    {
        foreach (var (block, rect) in _blocks)
        {
            var r = block.Region;
            if (x < rect.Left) return r.OffsetBytes;
            if (x <= rect.Right)
                return r.OffsetBytes + (long)((x - rect.X) / Math.Max(1, rect.Width) * r.SizeBytes);
        }
        return _blocks.Count == 0 ? 0 : _blocks[^1].Block.Region.EndBytes;
    }

    private bool IsNearHandle(Point p) => HasHandle && Math.Abs(p.X - XFor(HandleOffset)) <= HandleGrab && p.Y <= BarHeight + 6;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsNearHandle(e.GetPosition(this)))
        {
            _dragging = CaptureMouse();
            e.Handled = true;
            return;
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragging)
        {
            var offset = OffsetFor(p.X);
            if (HandleDragCommand?.CanExecute(offset) == true) HandleDragCommand.Execute(offset);
            return;
        }
        Cursor = IsNearHandle(p) ? Cursors.SizeWE : IsSelectable ? Cursors.Hand : Cursors.Arrow;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = false;
        base.OnLostMouseCapture(e);
    }

    protected override DiskRegion? HitTestRegion(Point p) =>
        _blocks.FirstOrDefault(t => p.X >= t.Rect.Left && p.X < t.Rect.Right && p.Y <= BarHeight + LabelHeight).Block?.Region;

    protected override string? DescribeAt(Point p)
    {
        if (IsNearHandle(p)) return "Drag to change the planned size (preview only)";
        var hit = _blocks.FirstOrDefault(t => p.X >= t.Rect.Left && p.X < t.Rect.Right && p.Y <= BarHeight + LabelHeight);
        return hit.Block is null ? null : RegionText(hit.Block.Region);
    }
}
