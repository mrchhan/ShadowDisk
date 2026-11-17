using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ShadowDisk.Core;
using ShadowDisk.Core.Layout;
using ShadowDisk.Core.Model;
using ShadowDisk.Core.Moving;
using ShadowDisk.Core.Planning;

namespace ShadowDisk.App.Controls;

/// <summary>The divider between <paramref name="Left"/> and <paramref name="Right"/> was dragged to <paramref name="NewBoundary"/> (bytes). A preview gesture only.</summary>
public sealed record BoundaryDrag(DiskRegion Left, DiskRegion Right, long NewBoundary);

/// <summary>A whole drive block was dragged so it would start at <paramref name="NewOffset"/> (bytes). A preview gesture only.</summary>
public sealed record BlockMove(DiskRegion Region, long NewOffset);

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
    private const double BarRadius = 8;

    private static readonly Brush Sheen = CreateSheen();

    private static Brush CreateSheen()
    {
        var brush = new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90);
        brush.Freeze();
        return brush;
    }

    public static readonly DependencyProperty HighlightStartProperty = DependencyProperty.Register(
        nameof(HighlightStart), typeof(long), typeof(DiskBarView), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightLengthProperty = DependencyProperty.Register(
        nameof(HighlightLength), typeof(long), typeof(DiskBarView), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HandleOffsetProperty = DependencyProperty.Register(
        nameof(HandleOffset), typeof(long), typeof(DiskBarView), new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HandleDragCommandProperty = DependencyProperty.Register(
        nameof(HandleDragCommand), typeof(ICommand), typeof(DiskBarView), new PropertyMetadata(null));

    public static readonly DependencyProperty BoundaryDragCommandProperty = DependencyProperty.Register(
        nameof(BoundaryDragCommand), typeof(ICommand), typeof(DiskBarView), new PropertyMetadata(null));

    /// <summary>
    /// When set, the divider between two neighbouring data volumes can be dragged, like in other partition tools.
    /// On release it is executed with a <see cref="BoundaryDrag"/>; it never changes a disk itself (the wizard does, after confirmation).
    /// </summary>
    public ICommand? BoundaryDragCommand
    {
        get => (ICommand?)GetValue(BoundaryDragCommandProperty);
        set => SetValue(BoundaryDragCommandProperty, value);
    }

    public static readonly DependencyProperty BlockMoveCommandProperty = DependencyProperty.Register(
        nameof(BlockMoveCommand), typeof(ICommand), typeof(DiskBarView), new PropertyMetadata(null));

    /// <summary>
    /// When set, a whole drive block can be dragged along the bar into the free space next to it (MiniTool-style).
    /// The first drive and the Windows drive are locked and never move. On release it is executed with a <see cref="BlockMove"/>;
    /// it never changes a disk itself (the move window does, after confirmation).
    /// </summary>
    public ICommand? BlockMoveCommand
    {
        get => (ICommand?)GetValue(BlockMoveCommandProperty);
        set => SetValue(BlockMoveCommandProperty, value);
    }

    private DiskRegion? _moveRegion;     // block under the mouse when the button went down (a drag starts after a few pixels)
    private MoveRange? _moveRange;
    private Point _moveDown;
    private bool _moving;
    private long _moveGhost;             // where the block would start (bytes)
    private long _spanStart;             // bytes at the left of the area the block can move in
    private double _spanLeftPx, _spanPx; // linear scale over that area

    private const double BoundaryGrab = 7;
    private int _hoverBoundary = -1;
    private int _boundary = -1;      // index of the block left of the divider being dragged
    private long _ghost;             // where the divider is being dragged to (bytes)
    private long _ghostMin;
    private long _ghostMax;
    private double _pairLeft, _pairRight, _pairPx;   // the dragged pair's pixels, for a linear scale across both blocks
    private long _pairOffset;

    private static readonly Brush LockHatch = CreateLockHatch();

    private static Brush CreateLockHatch()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1.2);
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, Geometry.Parse("M0,6 L6,0")))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The parts that can't be given away: each drive's files plus its safety margin, hatched with a lock.
    /// The divider can't cross them, so the block ends show the limits while dragging.
    /// </summary>
    private void DrawLockedZones(DrawingContext dc, Func<long, double> dragX)
    {
        var lockLeft = new Rect(_pairLeft, 1, Math.Max(0, dragX(_ghostMin) - _pairLeft), BarHeight);
        var lockRight = new Rect(dragX(_ghostMax), 1, Math.Max(0, _pairRight - dragX(_ghostMax)), BarHeight);
        var split = dragX(_ghost);
        var rightStart = new Rect(split, 1, Math.Max(0, lockRight.Width), BarHeight);   // the right drive's files move to its new start
        foreach (var zone in new[] { lockLeft, rightStart })
        {
            if (zone.Width < 1) continue;
            dc.DrawRectangle(LockHatch, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash }, zone);
            if (zone.Width > 20)
            {
                var lockIcon = new FormattedText("\uE72E", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe MDL2 Assets"), 13, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(lockIcon, new Point(zone.X + zone.Width / 2 - lockIcon.Width / 2, zone.Y + BarHeight / 2 - lockIcon.Height / 2));
            }
        }

        // Both blocks taking part are outlined; the edge glows when it reaches a limit.
        dc.DrawRectangle(null, new Pen(SelectionBrush, 2.5), new Rect(_pairLeft + 1.25, 2.25, Math.Max(0, _pairRight - _pairLeft - 2.5), BarHeight - 2.5));
        var atLimit = _ghost <= _ghostMin || _ghost >= _ghostMax;
        dc.DrawLine(new Pen(atLimit ? DangerBrushOr(Brushes.OrangeRed) : SelectionBrush, atLimit ? 4 : 3), new Point(split, 1), new Point(split, BarHeight + 1));
    }

    /// <summary>The area the block can move in (free space), the block at its new place, and locks on drives that never move.</summary>
    private void DrawBlockMove(DrawingContext dc)
    {
        if (_moveRegion?.Partition is not { } p || _moveRange is not { } range) return;
        foreach (var (block, rect) in _blocks)
        {
            if (block.Region.Partition is not { } other || ReferenceEquals(other, p) || Layout is null) continue;
            if (MovePlanner.WhyLocked(Layout.Disk, other) is null) continue;
            dc.DrawRectangle(LockHatch, null, rect);
            if (rect.Width > 20)
            {
                var lockIcon = new FormattedText("\uE72E", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe MDL2 Assets"), 13, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(lockIcon, new Point(rect.X + rect.Width / 2 - lockIcon.Width / 2, rect.Y + BarHeight / 2 - lockIcon.Height / 2));
            }
        }

        var spanWidth = (range.MaxOffset + p.SizeBytes - _spanStart) * _spanPx;
        var span = new Rect(_spanLeftPx, 1, Math.Max(1, spanWidth), BarHeight);
        dc.DrawRectangle(UnallocatedBrush, new Pen(SelectionBrush, 1) { DashStyle = DashStyles.Dash }, span);

        var ghost = new Rect(_spanLeftPx + (_moveGhost - _spanStart) * _spanPx, 1, Math.Max(2, p.SizeBytes * _spanPx), BarHeight);
        dc.DrawRectangle(FreeBrush, null, ghost);
        var used = p.Volume?.UsedBytes ?? 0;
        dc.DrawRectangle(UsedBrush, null, new Rect(ghost.X, ghost.Y, Math.Min(ghost.Width, used * _spanPx), ghost.Height));
        var atLimit = _moveGhost <= range.MinOffset || _moveGhost >= range.MaxOffset;
        dc.DrawRectangle(null, new Pen(atLimit ? DangerBrushOr(Brushes.OrangeRed) : SelectionBrush, 3), Rect.Inflate(ghost, -1.5, -1.5));

        var distance = _moveGhost - p.OffsetBytes;
        var text = Text(distance == 0 ? $"{p.DisplayName}  ◂ drag ▸" : $"{p.DisplayName}  {(distance < 0 ? "◂ left" : "right ▸")} {ByteSize.Format(Math.Abs(distance))}",
            12, Brushes.White, FontWeights.SemiBold);
        var bubble = new Rect(Math.Clamp(ghost.X + ghost.Width / 2 - text.Width / 2 - 8, 0, Math.Max(0, ActualWidth - text.Width - 16)), 6, text.Width + 16, text.Height + 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x18, 0x2C)), new Pen(SelectionBrush, 1), bubble, 6, 6);
        dc.DrawText(text, new Point(bubble.X + 8, bubble.Y + 3));
    }

    private void StartBlockCandidate(Point point)
    {
        _moveRegion = null;
        _moveRange = null;
        if (BlockMoveCommand is null || !IsSelectable || Layout is null || point.Y > BarHeight + 2) return;
        var hit = _blocks.FirstOrDefault(t => point.X >= t.Rect.Left && point.X < t.Rect.Right);
        if (hit.Block?.Region.Partition is not { Volume: not null } p) return;
        var range = MovePlanner.Range(Layout, p);
        if (!range.CanMove) return;
        _moveRegion = hit.Block.Region;
        _moveRange = range;
        _moveDown = point;
    }

    private void BeginBlockMove()
    {
        if (_moveRegion?.Partition is not { } p || _moveRange is not { } range || !CaptureMouse()) return;
        _spanStart = range.MinOffset;
        var spanEnd = range.MaxOffset + p.SizeBytes;
        _spanLeftPx = XFor(_spanStart);
        _spanPx = (XFor(spanEnd) - _spanLeftPx) / Math.Max(1, spanEnd - _spanStart);
        if (_spanPx <= 0) { ReleaseMouseCapture(); return; }
        _moveGhost = p.OffsetBytes;
        _moving = true;
        Focus();
        InvalidateVisual();
    }

    private void EndBlockMove(bool apply)
    {
        var region = _moveRegion;
        var ghost = _moveGhost;
        var wasMoving = _moving;
        _moving = false;
        _moveRegion = null;
        _moveRange = null;
        if (!wasMoving) return;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        if (!apply || region is null || Math.Abs(ghost - region.OffsetBytes) < ByteSize.MiB) return;
        var move = new BlockMove(region, ghost);
        if (BlockMoveCommand?.CanExecute(move) == true) BlockMoveCommand.Execute(move);
    }

    private Brush DangerBrushOr(Brush fallback) => TryFindResource("DangerBrush") as Brush ?? fallback;

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
        var divider = new Pen(PanelBrush, 2);
        var outline = new Pen(LineBrush, 1);
        var x = 1.0;

        // Sweep-in animation: reveal the bar from left to right.
        var reveal = Math.Clamp(Reveal, 0, 1);
        if (reveal < 1) dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth * reveal, ActualHeight)));

        // Rounded ends for the bar (the labels underneath are not clipped).
        var barRect = new Rect(1, 1, width, BarHeight);
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Union,
            new RectangleGeometry(barRect, BarRadius, BarRadius),
            new RectangleGeometry(new Rect(0, BarHeight + 2, Math.Max(1, ActualWidth), LabelHeight + 10))));

        // While a divider is dragged, the two blocks are drawn at their new sizes (linear scale across the pair),
        // with each drive's used data kept at its start, like other partition tools.
        var dragging = _boundary >= 0 && _boundary + 1 < blocks.Count;
        if (dragging)
        {
            var start = 1.0;
            for (var k = 0; k < _boundary; k++) start += widths[k];
            _pairLeft = start;
            _pairRight = start + widths[_boundary] + widths[_boundary + 1];
            _pairOffset = blocks[_boundary].Region.OffsetBytes;
            var pairBytes = blocks[_boundary + 1].Region.EndBytes - _pairOffset;
            _pairPx = pairBytes > 0 ? (_pairRight - _pairLeft) / pairBytes : 0;
        }
        double DragX(long offset) => _pairLeft + (offset - _pairOffset) * _pairPx;

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var rect = new Rect(x, 1, widths[i], BarHeight);
            _blocks.Add((b, rect));   // hit-testing keeps the real layout

            var shown = rect;
            var sizeBytes = b.Region.SizeBytes;
            double? usedOverride = null;
            if (dragging && (i == _boundary || i == _boundary + 1))
            {
                var split = DragX(_ghost);
                shown = i == _boundary
                    ? new Rect(_pairLeft, 1, Math.Max(1, split - _pairLeft), BarHeight)
                    : new Rect(split, 1, Math.Max(1, _pairRight - split), BarHeight);
                sizeBytes = i == _boundary ? _ghost - b.Region.OffsetBytes : b.Region.EndBytes - _ghost;
                usedOverride = b.UsedBytes * _pairPx;
            }
            rect = shown;

            if (b.Region.Partition is null)
            {
                dc.DrawRectangle(UnallocatedBrush, null, rect);
            }
            else if (b.Region.Partition.Volume is not null)
            {
                var usedWidth = Math.Min(rect.Width, usedOverride ?? rect.Width * b.UsedFraction * UsedGrow);
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
                var name = Text(b.Label, 12, TextBrush, FontWeights.SemiBold);
                name.MaxTextWidth = rect.Width - 6;
                name.MaxTextHeight = 18;
                name.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(name, new Point(rect.X + 3, BarHeight + 5));

                var size = Text(ByteSize.Format(sizeBytes), 11, usedOverride is null ? MutedBrush : SelectionBrush, FontWeights.Normal);
                size.MaxTextWidth = rect.Width - 6;
                size.MaxTextHeight = 16;
                size.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(size, new Point(rect.X + 3, BarHeight + 22));
            }
            x += widths[i];
        }

        if (dragging) DrawLockedZones(dc, DragX);
        if (_moving) DrawBlockMove(dc);

        // Glass sheen over the top of the bar.
        dc.DrawRoundedRectangle(Sheen, null, new Rect(1, 1, width, BarHeight * 0.5), BarRadius, BarRadius);
        dc.Pop();
        dc.DrawRoundedRectangle(null, outline, barRect, BarRadius, BarRadius);
        if (reveal < 1) dc.Pop();

        DrawBoundaries(dc);

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
            if (selected.Block is not null && _boundary < 0 && !_moving)   // while dragging, the moving pair is outlined instead
            {
                // A soft glow that breathes around the selected block.
                dc.PushOpacity(0.12 + 0.38 * Pulse);
                dc.DrawRoundedRectangle(null, new Pen(SelectionBrush, 4 + 4 * Pulse), Rect.Inflate(selected.Rect, 0.5, 0.5), 3, 3);
                dc.Pop();
                dc.DrawRectangle(null, new Pen(SelectionBrush, 3), Rect.Inflate(selected.Rect, -1.5, -1.5));
            }
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

    // ===== Drag-to-rebalance: the divider between two neighbouring data volumes =====

    private static bool IsMovableVolume(DiskRegion r) =>
        r.Partition is { Kind: PartitionKind.Basic or PartitionKind.Logical, Volume.DriveLetter.Length: > 0 } p && !p.IsSystem && !p.IsStartupPartition;

    /// <summary>Indexes i where the divider between block i and i+1 can be dragged.</summary>
    private IEnumerable<int> Boundaries()
    {
        if (!IsSelectable || BoundaryDragCommand is null) yield break;
        for (var i = 0; i + 1 < _blocks.Count; i++)
        {
            var left = _blocks[i].Block.Region;
            var right = _blocks[i + 1].Block.Region;
            if (IsMovableVolume(left) && IsMovableVolume(right) && right.Partition!.IsBoot == false) yield return i;
        }
    }

    private int BoundaryAt(Point p) =>
        p.Y > BarHeight + 2 ? -1 : Boundaries().Where(i => Math.Abs(p.X - _blocks[i].Rect.Right) <= BoundaryGrab).DefaultIfEmpty(-1).First();

    private void DrawBoundaries(DrawingContext dc)
    {
        foreach (var i in Boundaries())
        {
            var active = i == _boundary || i == _hoverBoundary;
            var x = _boundary == i ? _pairLeft + (_ghost - _pairOffset) * _pairPx : _blocks[i].Rect.Right;
            var grip = new Rect(x - 3.5, BarHeight / 2 - 13, 7, 26);
            dc.DrawRoundedRectangle(active ? SelectionBrush : PanelBrush, new Pen(active ? Brushes.White : LineBrush, 1), grip, 3.5, 3.5);
            var dots = new Pen(active ? Brushes.White : MutedBrush, 1);
            for (var k = -1; k <= 1; k++) dc.DrawLine(dots, new Point(x - 1.5, BarHeight / 2 + k * 5), new Point(x + 1.5, BarHeight / 2 + k * 5));
        }
        if (_boundary < 0) return;

        // The ghost: where the divider will be, the space that changes hands, and the two new sizes.
        var left = _blocks[_boundary].Block.Region;
        var right = _blocks[_boundary + 1].Block.Region;
        var newX = _pairLeft + (_ghost - _pairOffset) * _pairPx;

        var leftSize = _ghost - left.OffsetBytes;
        var rightSize = right.EndBytes - _ghost;
        var text = Text($"{left.Partition!.DisplayName} {ByteSize.Format(leftSize)}   ◂ ▸   {right.Partition!.DisplayName} {ByteSize.Format(rightSize)}",
            12, Brushes.White, FontWeights.SemiBold);
        var bubble = new Rect(Math.Clamp(newX - text.Width / 2 - 8, 0, Math.Max(0, ActualWidth - text.Width - 16)), 6, text.Width + 16, text.Height + 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x18, 0x2C)), new Pen(SelectionBrush, 1), bubble, 6, 6);
        dc.DrawText(text, new Point(bubble.X + 8, bubble.Y + 3));
    }

    private bool StartBoundaryDrag(Point p)
    {
        var i = BoundaryAt(p);
        if (i < 0 || Layout is null || !CaptureMouse()) return false;
        var left = _blocks[i].Block.Region.Partition!;
        var right = _blocks[i + 1].Block.Region.Partition!;
        // Rough limits for the gesture (the wizard asks Windows for the exact ones): each volume keeps its files plus a little.
        var leftMin = left.IsBoot ? left.SizeBytes : MinSizeOf(left);
        _ghostMin = left.OffsetBytes + leftMin;
        _ghostMax = right.EndBytes - MinSizeOf(right);
        _ghost = left.EndBytes;
        _boundary = i;
        InvalidateVisual();
        return true;
    }

    private long MinSizeOf(Partition p)
    {
        var limits = PlanLimits.Estimate(Layout!, p, PlanKind.ShrinkVolume);
        return limits.IsUsable ? Math.Max(PlanLimits.AlignUp(limits.MinBytes), 16 * ByteSize.MiB) : p.SizeBytes;
    }

    private void EndBoundaryDrag(bool apply)
    {
        if (_boundary < 0) return;
        var i = _boundary;
        _boundary = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        if (!apply || i + 1 >= _blocks.Count) return;
        var drag = new BoundaryDrag(_blocks[i].Block.Region, _blocks[i + 1].Block.Region, _ghost);
        if (Math.Abs(_ghost - drag.Left.EndBytes) >= 16 * ByteSize.MiB && BoundaryDragCommand?.CanExecute(drag) == true)
            BoundaryDragCommand.Execute(drag);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _moving)
        {
            EndBlockMove(apply: false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _boundary >= 0)
        {
            EndBoundaryDrag(apply: false);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
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
        if (StartBoundaryDrag(e.GetPosition(this)))
        {
            Focus();
            e.Handled = true;
            return;
        }
        if (IsNearHandle(e.GetPosition(this)))
        {
            _dragging = CaptureMouse();
            e.Handled = true;
            return;
        }
        StartBlockCandidate(e.GetPosition(this));
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_moveRegion is not null && !_moving && e.LeftButton == MouseButtonState.Pressed && Math.Abs(p.X - _moveDown.X) > 4)
            BeginBlockMove();
        if (_moving && _moveRegion?.Partition is { } moving && _moveRange is { } range)
        {
            var offset = moving.OffsetBytes + (long)((p.X - _moveDown.X) / _spanPx);
            _moveGhost = Math.Clamp(PlanLimits.AlignDown(Math.Max(0, offset)), range.MinOffset, range.MaxOffset);
            if (Math.Abs(_moveGhost - moving.OffsetBytes) < ByteSize.MiB) _moveGhost = moving.OffsetBytes;   // snap back to "no move"
            Cursor = Cursors.SizeAll;
            InvalidateVisual();
            return;
        }
        if (_boundary >= 0)
        {
            var offset = _pairPx > 0 ? _pairOffset + (long)((p.X - _pairLeft) / _pairPx) : OffsetFor(p.X);
            _ghost = Math.Clamp(PlanLimits.AlignDown(offset), Math.Min(_ghostMin, _ghostMax), Math.Max(_ghostMin, _ghostMax));
            InvalidateVisual();
            return;
        }
        var hover = BoundaryAt(p);
        if (hover != _hoverBoundary)
        {
            _hoverBoundary = hover;
            InvalidateVisual();
        }
        if (hover >= 0)
        {
            Cursor = Cursors.SizeWE;
            base.OnMouseMove(e);
            return;
        }
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
        if (_moving)
        {
            EndBlockMove(apply: true);
            e.Handled = true;
            return;
        }
        _moveRegion = null;
        if (_boundary >= 0)
        {
            EndBoundaryDrag(apply: true);
            e.Handled = true;
            return;
        }
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverBoundary >= 0)
        {
            _hoverBoundary = -1;
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_moving)
        {
            _moving = false;
            _moveRegion = null;
            InvalidateVisual();
        }
        if (_boundary >= 0)
        {
            _boundary = -1;
            InvalidateVisual();
        }
        _dragging = false;
        base.OnLostMouseCapture(e);
    }

    protected override DiskRegion? HitTestRegion(Point p) =>
        _blocks.FirstOrDefault(t => p.X >= t.Rect.Left && p.X < t.Rect.Right && p.Y <= BarHeight + LabelHeight).Block?.Region;

    protected override string? DescribeAt(Point p)
    {
        if (IsNearHandle(p)) return "Drag to change the planned size (preview only)";
        if (BoundaryAt(p) is >= 0 and var b)
            return $"Drag to move space between {_blocks[b].Block.Label} and {_blocks[b + 1].Block.Label}.\n" +
                   "Nothing changes yet: a wizard opens with the amount you chose, and you confirm every step.";
        var hit = _blocks.FirstOrDefault(t => p.X >= t.Rect.Left && p.X < t.Rect.Right && p.Y <= BarHeight + LabelHeight);
        if (hit.Block is null) return null;
        var text = RegionText(hit.Block.Region);
        if (BlockMoveCommand is not null && Layout is not null && hit.Block.Region.Partition is { Volume: not null } part)
        {
            var range = MovePlanner.Range(Layout, part);
            text += range.CanMove ? "\nDrag the block to move this drive into the free space next to it." : $"\n🔒 {range.Locked}";
        }
        return text;
    }
}
