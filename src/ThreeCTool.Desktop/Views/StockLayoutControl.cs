using System.Globalization;
using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

using ThreeCTool.Core.Projects;
using ThreeCTool.Desktop.ViewModels;

namespace ThreeCTool.Desktop.Views;

public enum StockLayoutView
{
    /// <summary>Seen from above: X to the right, Y up; X and Y seams.</summary>
    Top,

    /// <summary>Seen from the front: X to the right, Z up; X seams and layers.</summary>
    Front,
}

/// <summary>
/// Draws a split box stock with its pieces, seams and the part outline. Seams can be dragged; the move is clamped so every
/// piece stays within the standard size and is passed to <see cref="MoveSeamCommand"/> as a <see cref="SeamMove"/> on release.
/// </summary>
public sealed class StockLayoutControl : Control
{
    public static readonly StyledProperty<StockLayout?> LayoutDataProperty =
        AvaloniaProperty.Register<StockLayoutControl, StockLayout?>(nameof(LayoutData));

    public static readonly StyledProperty<StockLayoutView> ViewProperty =
        AvaloniaProperty.Register<StockLayoutControl, StockLayoutView>(nameof(View));

    public static readonly StyledProperty<ICommand?> MoveSeamCommandProperty =
        AvaloniaProperty.Register<StockLayoutControl, ICommand?>(nameof(MoveSeamCommand));

    private const double Padding = 8;
    private const double HitDistance = 5;

    private static readonly IBrush StockBrush = new ImmutableSolidColorBrush(Color.FromArgb(70, 200, 160, 100));
    private static readonly IPen OutlinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(140, 110, 70)), 1.5);
    private static readonly IPen SeamPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(224, 122, 31)), 2);
    private static readonly IPen ActiveSeamPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(240, 70, 30)), 3);
    private static readonly IPen PartPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(70, 130, 220)), 1.5, new ImmutableDashStyle([4, 3], 0));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(128, 128, 128));

    private Stock? preview;
    private (StockAxis Axis, int Index)? drag;
    private (StockAxis Axis, int Index)? hover;

    static StockLayoutControl()
    {
        AffectsRender<StockLayoutControl>(LayoutDataProperty, ViewProperty);
    }

    public StockLayout? LayoutData
    {
        get => GetValue(LayoutDataProperty);
        set => SetValue(LayoutDataProperty, value);
    }

    public StockLayoutView View
    {
        get => GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    public ICommand? MoveSeamCommand
    {
        get => GetValue(MoveSeamCommandProperty);
        set => SetValue(MoveSeamCommandProperty, value);
    }

    private StockAxis Vertical => View == StockLayoutView.Top ? StockAxis.Y : StockAxis.Z;

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Shown() is not { } stock || Map(stock) is not { } map)
        {
            return;
        }

        var (u, v) = (StockTiling.Length(stock, StockAxis.X), StockTiling.Length(stock, Vertical));
        var outline = new Rect(map.Screen(0, v), map.Screen(u, 0));
        context.FillRectangle(StockBrush, outline);
        context.DrawRectangle(OutlinePen, outline);

        if (LayoutData?.Part is { } part)
        {
            var (minV, maxV) = View == StockLayoutView.Top ? (part.Min.Y - stock.OriginY, part.Max.Y - stock.OriginY) : (part.Min.Z - stock.OriginZ, part.Max.Z - stock.OriginZ);
            context.DrawRectangle(PartPen, new Rect(map.Screen(part.Min.X - stock.OriginX, maxV), map.Screen(part.Max.X - stock.OriginX, minV)));
        }

        foreach (var axis in new[] { StockAxis.X, Vertical })
        {
            var seams = StockTiling.Seams(stock, axis);
            for (var i = 0; i < seams.Count; i++)
            {
                var pen = drag == (axis, i) || hover == (axis, i) ? ActiveSeamPen : SeamPen;
                if (axis == StockAxis.X)
                {
                    context.DrawLine(pen, map.Screen(seams[i], 0), map.Screen(seams[i], v));
                }
                else
                {
                    context.DrawLine(pen, map.Screen(0, seams[i]), map.Screen(u, seams[i]));
                }
            }
        }

        DrawNumbers(context, stock, map);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == LayoutDataProperty && drag is null)
        {
            preview = null;
            hover = null;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        if (LayoutData is not { } layout || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || HitSeam(e.GetPosition(this)) is not { } seam)
        {
            return;
        }

        preview = Copy(layout.Stock);
        drag = seam;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (drag is { } seam && preview is not null && Map(preview) is { } map)
        {
            StockTiling.MoveSeam(preview, seam.Axis, seam.Index, map.Stock(position, seam.Axis == StockAxis.X));
            InvalidateVisual();
            return;
        }

        var hit = HitSeam(position);
        if (hit != hover)
        {
            hover = hit;
            Cursor = hit is { Axis: StockAxis.X } ? new Cursor(StandardCursorType.SizeWestEast)
                : hit is not null ? new Cursor(StandardCursorType.SizeNorthSouth)
                : null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (drag is not { } seam || preview is null)
        {
            return;
        }

        drag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        var move = new SeamMove(seam.Axis, seam.Index, StockTiling.Seams(preview, seam.Axis)[seam.Index]);
        if (MoveSeamCommand is { } command && command.CanExecute(move))
        {
            command.Execute(move);
        }
        else
        {
            preview = null;
        }

        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (drag is not null)
        {
            drag = null;
            preview = null;
            InvalidateVisual();
        }
    }

    private static Stock Copy(Stock stock) => new()
    {
        Type = stock.Type,
        OriginX = stock.OriginX,
        OriginY = stock.OriginY,
        OriginZ = stock.OriginZ,
        SizeX = stock.SizeX,
        SizeY = stock.SizeY,
        SizeZ = stock.SizeZ,
        PieceName = stock.PieceName,
        PieceSizeX = stock.PieceSizeX,
        PieceSizeY = stock.PieceSizeY,
        PieceSizeZ = stock.PieceSizeZ,
        SeamsX = [.. stock.SeamsX],
        SeamsY = [.. stock.SeamsY],
        SeamsZ = [.. stock.SeamsZ],
    };

    private Stock? Shown() => preview ?? LayoutData?.Stock;

    /// <summary>Uniform scale fitting the stock into the control, centred.</summary>
    private Mapping? Map(Stock stock)
    {
        var u = StockTiling.Length(stock, StockAxis.X);
        var v = StockTiling.Length(stock, Vertical);
        var width = Bounds.Width - (2 * Padding);
        var height = Bounds.Height - (2 * Padding);
        if (u <= 0 || v <= 0 || width < 1 || height < 1)
        {
            return null;
        }

        var scale = Math.Min(width / u, height / v);
        return new Mapping(Padding + ((width - (u * scale)) / 2), Padding + ((height - (v * scale)) / 2), scale, v);
    }

    private (StockAxis Axis, int Index)? HitSeam(Point point)
    {
        if (LayoutData is not { } layout || Map(layout.Stock) is not { } map)
        {
            return null;
        }

        var stock = layout.Stock;
        var (u, v) = (StockTiling.Length(stock, StockAxis.X), StockTiling.Length(stock, Vertical));
        var (x, y) = (map.Stock(point, true), map.Stock(point, false));
        var slack = HitDistance / map.Scale;
        (StockAxis, int)? best = null;
        var bestDistance = slack;
        foreach (var axis in new[] { StockAxis.X, Vertical })
        {
            var seams = StockTiling.Seams(stock, axis);
            var (along, across, acrossLength) = axis == StockAxis.X ? (x, y, v) : (y, x, u);
            if (across < -slack || across > acrossLength + slack)
            {
                continue;
            }

            for (var i = 0; i < seams.Count; i++)
            {
                var distance = Math.Abs(seams[i] - along);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = (axis, i);
                }
            }
        }

        return best;
    }

    /// <summary>Piece numbers in each cell; pieces stacked behind each other in this view are listed together.</summary>
    private void DrawNumbers(DrawingContext context, Stock stock, Mapping map)
    {
        if (!StockTiling.IsTiled(stock))
        {
            return;
        }

        var typeface = new Typeface(FontFamily.Default);
        var cells = StockTiling.Pieces(stock).GroupBy(p => (p.Column, View == StockLayoutView.Top ? p.Row : p.Layer));
        foreach (var cell in cells)
        {
            var first = cell.First();
            var (minV, maxV) = View == StockLayoutView.Top
                ? (first.MinY - stock.OriginY, first.MaxY - stock.OriginY)
                : (first.MinZ - stock.OriginZ, first.MaxZ - stock.OriginZ);
            var centre = map.Screen((first.MinX + first.MaxX) / 2 - stock.OriginX, (minV + maxV) / 2);
            var text = new FormattedText(
                string.Join("/", cell.Select(p => p.Number.ToString(CultureInfo.InvariantCulture))),
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                11,
                LabelBrush);
            var cellWidth = first.SizeX * map.Scale;
            var cellHeight = (maxV - minV) * map.Scale;
            if (text.Width < cellWidth - 2 && text.Height < cellHeight - 2)
            {
                context.DrawText(text, new Point(centre.X - (text.Width / 2), centre.Y - (text.Height / 2)));
            }
        }
    }

    /// <summary>Stock-relative (u along X, v along the vertical axis) to screen coordinates and back.</summary>
    private readonly record struct Mapping(double Left, double Top, double Scale, double Height)
    {
        public Point Screen(double u, double v) => new(Left + (u * Scale), Top + ((Height - v) * Scale));

        public double Stock(Point point, bool horizontal) =>
            horizontal ? (point.X - Left) / Scale : Height - ((point.Y - Top) / Scale);
    }
}
