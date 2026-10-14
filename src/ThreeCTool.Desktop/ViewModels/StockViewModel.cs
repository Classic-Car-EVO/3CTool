using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Geometry;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>A seam dragged to a new place in the stock layout (position from the stock origin, mm).</summary>
public sealed record SeamMove(StockAxis Axis, int Index, double Position);

/// <summary>What the stock layout drawing shows: a detached copy of the stock, its pieces and the part outline.</summary>
public sealed record StockLayout(Stock Stock, IReadOnlyList<StockPiece> Pieces, BoundingBox? Part);

/// <summary>
/// The seams of one axis as text in the display unit (<c>600; 1200</c>), measured from the stock's left, front or bottom
/// edge. Valid seams that split the stock into pieces no longer than the standard size are written immediately.
/// </summary>
public sealed partial class SeamField : ObservableObject
{
    private readonly StockViewModel owner;
    private bool refreshing;

    public SeamField(StockViewModel owner, StockAxis axis, LengthUnit unit)
    {
        this.owner = owner;
        Axis = axis;
        Unit = unit;
        Label = axis switch
        {
            StockAxis.X => "X seams (from the left)",
            StockAxis.Y => "Y seams (from the front)",
            _ => "Z seams (from the bottom)",
        };
    }

    public StockAxis Axis { get; }

    public LengthUnit Unit { get; }

    public string Label { get; }

    public string UnitText => NumberField.Abbreviation(Unit);

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    public static bool TryParse(string text, LengthUnit unit, out List<double> seams)
    {
        seams = [];
        foreach (var entry in (text ?? string.Empty).Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Units.TryParseLength(entry, unit, out var mm) || !double.IsFinite(mm))
            {
                return false;
            }

            seams.Add(mm);
        }

        return true;
    }

    public static string Format(IEnumerable<double> seams, LengthUnit unit)
    {
        ArgumentNullException.ThrowIfNull(seams);
        return string.Join("; ", seams.Select(s => NumberField.FormatLength(s, unit)));
    }

    public void Refresh(Stock stock)
    {
        refreshing = true;
        try
        {
            Text = Format(StockTiling.Seams(stock, Axis), Unit);
            Error = null;
        }
        finally
        {
            refreshing = false;
        }
    }

    partial void OnTextChanged(string value)
    {
        if (refreshing)
        {
            return;
        }

        if (!TryParse(value, Unit, out var seams))
        {
            Error = $"{Label}: enter lengths separated by ';', e.g. 600; 1200.";
            return;
        }

        Error = owner.TrySetSeams(this, seams);
    }
}

/// <summary>
/// Stock shape and size (box, cylinder for the rotary axis, or a mesh file) and the standard material size a box stock
/// is made of; a stock larger than the standard size is split at seams into pieces. Changes are saved immediately.
/// </summary>
public sealed partial class StockViewModel : ObservableObject
{
    public const string FreeSizeLabel = "Free size (one block, no splitting)";

    private static readonly FileType Meshes = new("Stock mesh (STL, 3MF)", [.. MeshImporter.SupportedExtensions.Select(e => "*" + e)]);

    private readonly IShell shell;
    private readonly ProjectWorkspace workspace;
    private readonly Func<Task> geometryChanged;
    private IReadOnlyList<StockSize> library = [];
    private BoundingBox? partBounds;
    private double margin = 2;
    private bool loading;

    public StockViewModel(IShell shell, ProjectWorkspace workspace, Func<Task> geometryChanged)
    {
        this.shell = shell;
        this.workspace = workspace;
        this.geometryChanged = geometryChanged;
        Size = Sizes[0];
    }

    public static IReadOnlyList<Choice<StockType>> Types { get; } =
    [
        new("Box", StockType.Box),
        new("Cylinder (rotary, along X)", StockType.Cylinder),
        new("Mesh file (pre-shaped stock)", StockType.Mesh),
    ];

    [ObservableProperty]
    public partial Choice<StockType> Type { get; set; } = Types[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBox), nameof(IsCylinder), nameof(IsMesh))]
    public partial StockType Shown { get; private set; }

    public bool IsBox => Shown == StockType.Box;

    public bool IsCylinder => Shown == StockType.Cylinder;

    public bool IsMesh => Shown == StockType.Mesh;

    [ObservableProperty]
    public partial IReadOnlyList<NumberField> BoxFields { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<NumberField> CylinderFields { get; private set; } = [];

    [ObservableProperty]
    public partial NumberField? Margin { get; private set; }

    [ObservableProperty]
    public partial string MeshPath { get; private set; } = string.Empty;

    /// <summary>"Free size" and the library sizes for the project material (or any material).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<StockSize?>> Sizes { get; private set; } = [new(FreeSizeLabel, null)];

    [ObservableProperty]
    public partial Choice<StockSize?> Size { get; set; }

    /// <summary>The box is made of a standard size: show the seams and pieces.</summary>
    [ObservableProperty]
    public partial bool HasPieceSize { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SeamField> SeamFields { get; private set; } = [];

    [ObservableProperty]
    public partial string PiecesText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> PieceList { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTilingProblem))]
    public partial string? TilingProblem { get; private set; }

    public bool HasTilingProblem => TilingProblem is not null;

    [ObservableProperty]
    public partial StockLayout? Layout { get; private set; }

    public void Load(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var s = session.Stock;
        var unit = shell.Services.Settings.DisplayUnit;
        loading = true;
        try
        {
            Type = Types.First(t => t.Value == s.Type);
            Shown = s.Type;
            MeshPath = s.MeshPath ?? "(none)";
            BoxFields = Watch(
                NumberField.Length("Origin X (min)", () => s.OriginX, v => s.OriginX = v, unit),
                NumberField.Length("Origin Y (min)", () => s.OriginY, v => s.OriginY = v, unit),
                NumberField.Length("Origin Z (bottom)", () => s.OriginZ, v => s.OriginZ = v, unit),
                NumberField.PositiveLength("Size X", () => s.SizeX, v => s.SizeX = v, unit),
                NumberField.PositiveLength("Size Y", () => s.SizeY, v => s.SizeY = v, unit),
                NumberField.PositiveLength("Size Z", () => s.SizeZ, v => s.SizeZ = v, unit));
            CylinderFields = Watch(
                NumberField.Length("Start X", () => s.OriginX, v => s.OriginX = v, unit),
                NumberField.Length("Axis Y", () => s.OriginY, v => s.OriginY = v, unit),
                NumberField.Length("Axis Z", () => s.OriginZ, v => s.OriginZ = v, unit),
                NumberField.PositiveLength("Length", () => s.Length, v => s.Length = v, unit),
                NumberField.PositiveLength("Diameter", () => s.Diameter, v => s.Diameter = v, unit));
            Margin = NumberField.Length("Margin", () => margin, v => margin = v, unit);
            SeamFields = [.. StockTiling.Axes.Select(a => new SeamField(this, a, unit))];
            ShowSizes(session);
            ShowTiling(session.Stock, null);
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>Reads the standard sizes from the library (call after <see cref="Load"/>).</summary>
    public async Task LoadSizesAsync(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        library = await shell.Services.Library.ListStockSizesAsync();
        loading = true;
        try
        {
            ShowSizes(session);
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>Union of the milled models' bounds, drawn in the stock layout.</summary>
    public void SetPartBounds(BoundingBox? bounds)
    {
        partBounds = bounds is { IsEmpty: false } ? bounds : null;
        if (workspace.Session is { } session)
        {
            ShowTiling(session.Stock, SeamFields);
        }
    }

    /// <summary>
    /// Switches between box and cylinder keeping the stock where it was: the cylinder axis goes through the box centre
    /// (and back); sizes that were never set are taken from the other shape. Only box stock keeps its standard size.
    /// </summary>
    public static void ChangeType(Stock stock, StockType type)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (stock.Type == StockType.Box && type == StockType.Cylinder)
        {
            if (stock.Length <= 0 || stock.Diameter <= 0)
            {
                stock.Length = stock.SizeX;
                stock.Diameter = Math.Max(stock.SizeY, stock.SizeZ);
            }

            stock.OriginY += stock.SizeY / 2;
            stock.OriginZ += stock.SizeZ / 2;
        }
        else if (stock.Type == StockType.Cylinder && type == StockType.Box)
        {
            if (stock.SizeX <= 0 || stock.SizeY <= 0 || stock.SizeZ <= 0)
            {
                stock.SizeX = stock.Length;
                stock.SizeY = stock.SizeZ = stock.Diameter;
            }

            stock.OriginY -= stock.SizeY / 2;
            stock.OriginZ -= stock.SizeZ / 2;
        }

        stock.Type = type;
        StockTiling.Update(stock);
    }

    /// <summary>Label of a standard size such as "Plywood sheet (2440 × 1220 × 18 mm)".</summary>
    public static string SizeLabel(string name, double x, double y, double z, LengthUnit unit) =>
        $"{name} ({NumberField.FormatLength(x, unit)} × {NumberField.FormatLength(y, unit)} × {NumberField.FormatLength(z, unit)} {NumberField.Abbreviation(unit)})";

    /// <summary>
    /// Applies seams typed into <paramref name="field"/> when they split the stock into valid pieces; returns the
    /// problem otherwise (nothing is changed then).
    /// </summary>
    internal string? TrySetSeams(SeamField field, List<double> seams)
    {
        if (workspace.Session is not { } session)
        {
            return null;
        }

        var trial = EntityCopy.Clone(session.Stock);
        SetSeams(trial, field.Axis, seams);
        if (StockTiling.AxisProblem(trial, field.Axis) is { } problem)
        {
            return problem;
        }

        SetSeams(session.Stock, field.Axis, seams);
        _ = SaveTilingAsync(SeamFields.Where(f => f != field));
        return null;
    }

    partial void OnTypeChanged(Choice<StockType> value)
    {
        if (loading || value is null || workspace.Session is not { } session || session.Stock.Type == value.Value)
        {
            return;
        }

        if (value.Value == StockType.Mesh && string.IsNullOrEmpty(session.Stock.MeshPath))
        {
            _ = LoadMesh();
            return;
        }

        _ = ChangeAsync(s =>
        {
            ChangeType(s.Stock, value.Value);
            return s.SaveAsync();
        });
    }

    partial void OnSizeChanged(Choice<StockSize?> value)
    {
        if (loading || value is null || workspace.Session is null)
        {
            return;
        }

        if (value.Value is not { } size)
        {
            _ = ChangeAsync(s =>
            {
                StockTiling.Clear(s.Stock);
                return s.SaveAsync();
            });
            return;
        }

        if (Margin is { HasError: true })
        {
            shell.ShowMessage(Margin.Error!, isError: true);
            return;
        }

        _ = ChangeAsync(async s =>
        {
            var part = await s.MillPartAsync();
            StockTiling.Fit(s.Stock, size.Name, size.SizeX, size.SizeY, size.SizeZ, Corners(part), margin);
            await s.SaveAsync();
        });
    }

    [RelayCommand]
    private async Task LoadMesh()
    {
        var path = await shell.Files.OpenFileAsync("Stock mesh", [Meshes, FileType.Any]);
        if (path is null)
        {
            if (workspace.Session is { } session)
            {
                Load(session);
            }

            return;
        }

        await ChangeAsync(s => s.SetStockMeshAsync(path));
    }

    /// <summary>
    /// Sizes the box or cylinder around the milled models plus the margin; a box made of a standard size becomes whole
    /// pieces of it.
    /// </summary>
    [RelayCommand]
    private async Task FitToPart()
    {
        if (Margin is { HasError: true })
        {
            shell.ShowMessage(Margin.Error!, isError: true);
            return;
        }

        await ChangeAsync(async s =>
        {
            var part = await s.MillPartAsync();
            if (part.TriangleCount == 0)
            {
                throw new InvalidOperationException("Import a model to mill first.");
            }

            var stock = s.Stock;
            switch (stock.Type)
            {
                case StockType.Box when StockTiling.HasPieceSize(stock):
                    StockTiling.Fit(stock, stock.PieceName ?? string.Empty, stock.PieceSizeX, stock.PieceSizeY, stock.PieceSizeZ, Corners(part), margin);
                    break;
                case StockType.Box:
                    PartGeometry.FitBox(stock, part.Bounds, margin);
                    break;
                case StockType.Cylinder:
                    PartGeometry.FitCylinder(stock, part, margin);
                    break;
                default:
                    throw new InvalidOperationException("Mesh stock has a fixed shape; switch to a box or cylinder to fit it.");
            }

            await s.SaveAsync();
        });
    }

    /// <summary>Spaces the seams of every axis evenly with the fewest pieces.</summary>
    [RelayCommand]
    private async Task EvenSeams()
    {
        if (workspace.Session is { } session)
        {
            StockTiling.ResetSeams(session.Stock);
            await SaveTilingAsync(SeamFields);
        }
    }

    /// <summary>A seam was dragged in the layout drawing.</summary>
    [RelayCommand]
    private async Task MoveSeam(SeamMove move)
    {
        if (move is null || workspace.Session is not { } session || move.Index >= StockTiling.Seams(session.Stock, move.Axis).Count)
        {
            return;
        }

        var seams = StockTiling.Seams(session.Stock, move.Axis).ToList();
        var trial = EntityCopy.Clone(session.Stock);
        SetSeams(trial, move.Axis, seams);
        StockTiling.MoveSeam(trial, move.Axis, move.Index, move.Position);
        SetSeams(session.Stock, move.Axis, StockTiling.Seams(trial, move.Axis));
        await SaveTilingAsync(SeamFields);
    }

    private static (double X, double Y, double Z, double MaxX, double MaxY, double MaxZ)? Corners(Mesh part)
    {
        if (part.TriangleCount == 0)
        {
            return null;
        }

        var b = part.Bounds;
        return (b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);
    }

    /// <summary>Replaces the seam list (a new list so the change is detected when saving).</summary>
    private static void SetSeams(Stock stock, StockAxis axis, IEnumerable<double> seams)
    {
        List<double> copy = [.. seams];
        switch (axis)
        {
            case StockAxis.X:
                stock.SeamsX = copy;
                break;
            case StockAxis.Y:
                stock.SeamsY = copy;
                break;
            default:
                stock.SeamsZ = copy;
                break;
        }
    }

    private void ShowSizes(ProjectSession session)
    {
        var unit = shell.Services.Settings.DisplayUnit;
        var stock = session.Stock;
        var materialId = session.Project.MaterialId;
        var choices = new List<Choice<StockSize?>> { new(FreeSizeLabel, null) };
        choices.AddRange(library
            .Where(s => s.MaterialId is null || s.MaterialId == materialId)
            .Select(s => new Choice<StockSize?>(SizeLabel(s.Name, s.SizeX, s.SizeY, s.SizeZ, unit), s)));
        Choice<StockSize?> selected = choices[0];
        if (StockTiling.HasPieceSize(stock))
        {
            selected = choices.Skip(1).FirstOrDefault(c => Matches(c.Value!, stock))
                ?? choices.Skip(1).FirstOrDefault(c => SameSize(c.Value!, stock))
                ?? new Choice<StockSize?>(
                    SizeLabel(stock.PieceName ?? "Standard size", stock.PieceSizeX, stock.PieceSizeY, stock.PieceSizeZ, unit) + " - not in the library",
                    new StockSize { Name = stock.PieceName ?? string.Empty, SizeX = stock.PieceSizeX, SizeY = stock.PieceSizeY, SizeZ = stock.PieceSizeZ });
            if (!choices.Contains(selected))
            {
                choices.Add(selected);
            }
        }

        Sizes = choices;
        Size = selected;

        static bool SameSize(StockSize size, Stock stock) =>
            Math.Abs(size.SizeX - stock.PieceSizeX) < 1e-9 && Math.Abs(size.SizeY - stock.PieceSizeY) < 1e-9 && Math.Abs(size.SizeZ - stock.PieceSizeZ) < 1e-9;

        static bool Matches(StockSize size, Stock stock) => SameSize(size, stock) && string.Equals(size.Name, stock.PieceName, StringComparison.Ordinal);
    }

    /// <summary>Updates the seams, pieces and layout; <paramref name="fields"/> are re-read (null: all).</summary>
    private void ShowTiling(Stock stock, IEnumerable<SeamField>? fields)
    {
        var unit = shell.Services.Settings.DisplayUnit;
        HasPieceSize = StockTiling.HasPieceSize(stock);
        foreach (var field in fields ?? SeamFields)
        {
            field.Refresh(stock);
        }

        var pieces = StockTiling.Pieces(stock);
        var problems = StockTiling.Problems(stock);
        TilingProblem = problems.Count == 0 ? null : string.Join(" ", problems);
        if (!HasPieceSize)
        {
            PiecesText = string.Empty;
            PieceList = [];
        }
        else if (pieces.Count == 1)
        {
            PiecesText = $"The stock is one piece of {stock.PieceName}.";
            PieceList = [];
        }
        else
        {
            var counts = StockTiling.Axes.Select(a => StockTiling.Bounds(stock, a).Count - 1).ToArray();
            PiecesText = string.Create(
                CultureInfo.CurrentCulture,
                $"{pieces.Count} pieces of {stock.PieceName}: {counts[0]} along X × {counts[1]} along Y × {counts[2]} layers. Each piece is machined on its own (all setups and tools), with its own G-code files; glue them together afterwards.");
            PieceList = [.. pieces.Select(p =>
                $"{p.Label}: {p.Position}, {NumberField.FormatLength(p.SizeX, unit)} × {NumberField.FormatLength(p.SizeY, unit)} × {NumberField.FormatLength(p.SizeZ, unit)} {NumberField.Abbreviation(unit)}")];
        }

        Layout = stock.Type == StockType.Box ? new StockLayout(EntityCopy.Clone(stock), pieces, partBounds) : null;
    }

    private NumberField[] Watch(params NumberField[] fields)
    {
        foreach (var field in fields)
        {
            field.Changed += (_, _) => _ = SaveAsync();
        }

        return fields;
    }

    /// <summary>A size or origin field changed: keeps valid seams, re-spaces the others and saves.</summary>
    private async Task SaveAsync()
    {
        await workspace.EditAsync(s =>
        {
            StockTiling.Update(s.Stock);
            return s.SaveAsync();
        });
        if (workspace.Session is { } session)
        {
            ShowTiling(session.Stock, null);
        }

        await geometryChanged();
    }

    private async Task SaveTilingAsync(IEnumerable<SeamField> refresh)
    {
        await workspace.EditAsync(s => s.SaveAsync());
        if (workspace.Session is { } session)
        {
            ShowTiling(session.Stock, refresh);
        }

        await geometryChanged();
    }

    private async Task ChangeAsync(Func<ProjectSession, Task> change)
    {
        await workspace.EditAsync(change);
        if (workspace.Session is { } session)
        {
            Load(session);
        }

        await geometryChanged();
    }
}
