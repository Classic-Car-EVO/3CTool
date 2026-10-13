using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Geometry;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>Stock shape and size (box, cylinder for the rotary axis, or a mesh file); changes are saved immediately.</summary>
public sealed partial class StockViewModel(IShell shell, ProjectWorkspace workspace, Func<Task> geometryChanged) : ObservableObject
{
    private static readonly FileType Meshes = new("Stock mesh (STL, 3MF)", [.. MeshImporter.SupportedExtensions.Select(e => "*" + e)]);

    private double margin = 2;
    private bool loading;

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
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>
    /// Switches between box and cylinder keeping the stock where it was: the cylinder axis goes through the box centre
    /// (and back); sizes that were never set are taken from the other shape.
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

    /// <summary>Sizes the box or cylinder around the milled models plus the margin.</summary>
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

            switch (s.Stock.Type)
            {
                case StockType.Box:
                    PartGeometry.FitBox(s.Stock, part.Bounds, margin);
                    break;
                case StockType.Cylinder:
                    PartGeometry.FitCylinder(s.Stock, part, margin);
                    break;
                default:
                    throw new InvalidOperationException("Mesh stock has a fixed shape; switch to a box or cylinder to fit it.");
            }

            await s.SaveAsync();
        });
    }

    private NumberField[] Watch(params NumberField[] fields)
    {
        foreach (var field in fields)
        {
            field.Changed += (_, _) => _ = SaveAsync();
        }

        return fields;
    }

    private async Task SaveAsync()
    {
        await workspace.EditAsync(s => s.SaveAsync());
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
