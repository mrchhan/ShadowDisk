using System.Windows;
using System.Windows.Media;
using ShadowDisk.Core;
using ShadowDisk.Core.Layout;

namespace ShadowDisk.App.Controls;

/// <summary>
/// Horizontal bar in on-disk order. Each partition is a block; inside a volume the dark part is
/// used data and the light part is free space. Hatched blocks are unallocated disk space.
/// Very small partitions are drawn at a minimum width so they stay visible (not to scale).
/// </summary>
public sealed class DiskBarView : LayoutViewBase
{
    private const double BarHeight = 54;
    private const double LabelHeight = 40;
    private const double MinBlockWidth = 14;

    private List<(BarBlock Block, Rect Rect)> _blocks = [];

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

        var selected = _blocks.FirstOrDefault(t => ReferenceEquals(t.Block.Region, SelectedRegion));
        if (selected.Block is not null)
            dc.DrawRectangle(null, new Pen(SelectionBrush, 3), Rect.Inflate(selected.Rect, -1.5, -1.5));
    }

    protected override DiskRegion? HitTestRegion(Point p) =>
        _blocks.FirstOrDefault(t => p.X >= t.Rect.Left && p.X < t.Rect.Right && p.Y <= BarHeight + LabelHeight).Block?.Region;

    protected override string? DescribeAt(Point p)
    {
        var hit = _blocks.FirstOrDefault(t => p.X >= t.Rect.Left && p.X < t.Rect.Right && p.Y <= BarHeight + LabelHeight);
        return hit.Block is null ? null : RegionText(hit.Block.Region);
    }
}
