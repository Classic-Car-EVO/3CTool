using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Geometry;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>An orientation suggestion for the selected model.</summary>
public sealed record OrientationRow(OrientationCandidate Candidate, string Height)
{
    public string Text => $"{Candidate.Description}: {Candidate.ReachableFraction:P0} of the surface reachable, {Height} tall";
}

/// <summary>Models of the project: import, units, repair report, transforms, placement and orientation.</summary>
public sealed partial class PartsViewModel : ObservableObject
{
    private static readonly FileType Models = new("3D models (STL, 3MF)", [.. MeshImporter.SupportedExtensions.Select(e => "*" + e)]);

    private readonly IShell shell;
    private readonly ProjectWorkspace workspace;
    private readonly Func<Task> geometryChanged;
    private double moveX;
    private double moveY;
    private double moveZ;
    private double rotateAngle = 90;
    private double scaleFactor = 1;
    private IReadOnlyDictionary<Guid, BoundingBox> bounds = new Dictionary<Guid, BoundingBox>();
    private bool loading;

    public PartsViewModel(IShell shell, ProjectWorkspace workspace, Func<Task> geometryChanged)
    {
        this.shell = shell;
        this.workspace = workspace;
        this.geometryChanged = geometryChanged;
        RotateAngle = NumberField.Number("Angle (degrees)", () => rotateAngle, v => rotateAngle = v);
        ScaleFactor = NumberField.Number("Scale factor", () => scaleFactor, v => scaleFactor = v);
        CreateFields(LengthUnit.Millimeter);
    }

    public static IReadOnlyList<Choice<LengthUnit>> Units { get; } = SettingsViewModel.DisplayUnits;

    public static IReadOnlyList<Choice<BodyRole>> Roles { get; } =
    [
        new("Mill (machined part)", BodyRole.Mill),
        new("Print (reference only)", BodyRole.Print),
    ];

    public static IReadOnlyList<Choice<Vector3>> Axes { get; } =
    [
        new("X", Vector3.UnitX),
        new("Y", Vector3.UnitY),
        new("Z", Vector3.UnitZ),
    ];

    public static IReadOnlyList<Choice<MachiningMode>> Modes { get; } =
    [
        new("3-axis (top only)", MachiningMode.ThreeAxis),
        new("3+1 (indexed rotary)", MachiningMode.ThreePlusOne),
    ];

    public ObservableCollection<PartModel> Parts { get; } = [];

    public ObservableCollection<string> Transforms { get; } = [];

    public ObservableCollection<OrientationRow> Suggestions { get; } = [];

    /// <summary>Unit assumed for files that do not declare one (STL).</summary>
    [ObservableProperty]
    public partial Choice<LengthUnit> ImportUnit { get; set; } = Units[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial PartModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    public partial string SelectedName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Choice<BodyRole>? SelectedRole { get; set; }

    [ObservableProperty]
    public partial Choice<LengthUnit>? SelectedUnit { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RepairText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial NumberField MoveX { get; private set; }

    [ObservableProperty]
    public partial NumberField MoveY { get; private set; }

    [ObservableProperty]
    public partial NumberField MoveZ { get; private set; }

    public NumberField RotateAngle { get; }

    public NumberField ScaleFactor { get; }

    [ObservableProperty]
    public partial Choice<Vector3> RotateAxis { get; set; } = Axes[2];

    [ObservableProperty]
    public partial Choice<MachiningMode> Mode { get; set; } = Modes[0];

    private LengthUnit Unit => shell.Services.Settings.DisplayUnit;

    /// <summary>Re-reads the model list (keeping the selection when it still exists) and the display unit.</summary>
    public void Load(ProjectSession session, IReadOnlyDictionary<Guid, BoundingBox> partBounds)
    {
        ArgumentNullException.ThrowIfNull(session);
        bounds = partBounds;
        var keep = Selected?.Id;
        Parts.Clear();
        foreach (var part in session.Parts)
        {
            Parts.Add(part);
        }

        CreateFields(Unit);
        Selected = Parts.FirstOrDefault(p => p.Id == keep) ?? Parts.FirstOrDefault();
        ShowSelected();
    }

    /// <summary>Shows the selected model's name, unit, size, repairs and transform history.</summary>
    private void ShowSelected()
    {
        loading = true;
        try
        {
            Transforms.Clear();
            Suggestions.Clear();
            if (Selected is not { } part)
            {
                SelectedName = string.Empty;
                SelectedRole = null;
                SelectedUnit = null;
                SizeText = string.Empty;
                RepairText = string.Empty;
                return;
            }

            SelectedName = part.Name;
            SelectedRole = Roles.First(r => r.Value == part.Role);
            SelectedUnit = Units.First(u => u.Value == part.ImportUnit);
            foreach (var step in part.Transforms)
            {
                Transforms.Add(Describe(step, Unit));
            }

            SizeText = bounds.TryGetValue(part.Id, out var box) && !box.IsEmpty
                ? $"{Length(box.Size.X)} x {Length(box.Size.Y)} x {Length(box.Size.Z)}"
                : string.Empty;
            RepairText = Repair(part.RepairReport);
        }
        finally
        {
            loading = false;
        }
    }

    public static string Describe(TransformStep step, LengthUnit unit)
    {
        ArgumentNullException.ThrowIfNull(step);
        string L(double mm) => NumberField.FormatLength(mm, unit);
        var pivot = step.PivotX == 0 && step.PivotY == 0 && step.PivotZ == 0
            ? string.Empty
            : $" around ({L(step.PivotX)}, {L(step.PivotY)}, {L(step.PivotZ)})";
        return step.Kind switch
        {
            TransformKind.Translate => $"Move X {L(step.X)}, Y {L(step.Y)}, Z {L(step.Z)} {NumberField.Abbreviation(unit)}",
            TransformKind.Rotate => $"Rotate {step.AngleDegrees.ToString("0.##", CultureInfo.InvariantCulture)}° about {AxisName(step.X, step.Y, step.Z)}{pivot}",
            _ => $"Scale x{step.Factor.ToString("0.####", CultureInfo.InvariantCulture)}{pivot}",
        };
    }

    private static string AxisName(double x, double y, double z) => (x, y, z) switch
    {
        (1, 0, 0) => "X",
        (-1, 0, 0) => "-X",
        (0, 1, 0) => "Y",
        (0, -1, 0) => "-Y",
        (0, 0, 1) => "Z",
        (0, 0, -1) => "-Z",
        _ => string.Create(CultureInfo.InvariantCulture, $"({x:0.###}, {y:0.###}, {z:0.###})"),
    };

    private static string Repair(MeshRepairReport? report)
    {
        if (report is null)
        {
            return "Not checked.";
        }

        var fixes = new List<string>();
        void Add(int count, string what)
        {
            if (count > 0)
            {
                fixes.Add($"{count} {what}");
            }
        }

        Add(report.RemovedDuplicateTriangles, "duplicate triangles removed");
        Add(report.RemovedDegenerateTriangles, "degenerate triangles removed");
        Add(report.FlippedTriangles, "triangles re-oriented");
        Add(report.HolesFilled, "holes filled");
        Add(report.NonManifoldEdges, "non-manifold edges remain");
        var summary = fixes.Count == 0 ? "No repairs needed." : string.Join(", ", fixes) + ".";
        return summary + (report.IsWatertight ? " The mesh is watertight." : " The mesh is still open: check the toolpaths around the gaps.");
    }

    private string Length(double mm) => NumberField.FormatLength(mm, Unit) + " " + NumberField.Abbreviation(Unit);

    [MemberNotNull(nameof(MoveX), nameof(MoveY), nameof(MoveZ))]
    private void CreateFields(LengthUnit unit)
    {
        MoveX = NumberField.Length("Move X", () => moveX, v => moveX = v, unit);
        MoveY = NumberField.Length("Move Y", () => moveY, v => moveY = v, unit);
        MoveZ = NumberField.Length("Move Z", () => moveZ, v => moveZ = v, unit);
    }

    partial void OnSelectedChanged(PartModel? value)
    {
        if (!loading)
        {
            ShowSelected();
        }
    }

    partial void OnSelectedNameChanged(string value)
    {
        if (loading || Selected is not { } part || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var name = value.Trim();
        _ = workspace.EditAsync(s =>
        {
            part.Name = name;
            return s.SaveAsync();
        });
    }

    partial void OnSelectedRoleChanged(Choice<BodyRole>? value)
    {
        if (loading || Selected is not { } part || value is null || part.Role == value.Value)
        {
            return;
        }

        var role = value.Value;
        _ = EditGeometryAsync(s =>
        {
            part.Role = role;
            return s.SaveAsync();
        });
    }

    partial void OnSelectedUnitChanged(Choice<LengthUnit>? value)
    {
        if (loading || Selected is not { } part || value is null || part.ImportUnit == value.Value)
        {
            return;
        }

        _ = EditGeometryAsync(s => s.SetImportUnitAsync(part, value.Value));
    }

    [RelayCommand]
    private async Task Import()
    {
        var path = await shell.Files.OpenFileAsync("Import a model", [Models, FileType.Any]);
        if (path is null)
        {
            return;
        }

        PartImport? imported = null;
        var unit = ImportUnit.Value;
        if (!await EditGeometryAsync(async s => imported = await s.ImportPartAsync(path, unit)))
        {
            return;
        }

        var part = imported!;
        Selected = Parts.FirstOrDefault(p => p.Id == part.Model.Id);
        var size = part.Loaded.AllBodies().Bounds.Size;
        var message = $"Imported '{part.Model.Name}': {part.Loaded.Bodies.Count} bodies, {Length(size.X)} x {Length(size.Y)} x {Length(size.Z)}.";
        if (!part.UnitsDeclared)
        {
            message += $" The file has no units, so {NumberField.Abbreviation(unit)} was assumed; change the model's unit if the size is wrong.";
        }

        if (part.Model.RepairReport is { IsWatertight: false })
        {
            message += " The mesh is not watertight.";
        }

        shell.ShowMessage(message);
    }

    [RelayCommand]
    private async Task Remove()
    {
        if (Selected is not { } part
            || !await shell.ConfirmAsync("Remove model", $"Remove '{part.Name}' from the project?", "Remove", danger: true))
        {
            return;
        }

        await EditGeometryAsync(s => s.RemovePartAsync(part));
    }

    [RelayCommand]
    private Task Move() => WithSelected((s, part) =>
        s.AddTransformAsync(part, MeshTransforms.Translate(new Vector3((float)moveX, (float)moveY, (float)moveZ))), MoveX, MoveY, MoveZ);

    [RelayCommand]
    private Task Rotate() => WithSelected((s, part) =>
        s.RotateAboutCenterAsync(part, MeshTransforms.Rotate(RotateAxis.Value, rotateAngle)), RotateAngle);

    /// <summary>Quarter turn about the model centre; the parameter is the axis and direction, such as <c>X+</c>.</summary>
    [RelayCommand]
    private Task QuarterTurn(string axis)
    {
        var direction = Axes.First(a => a.Label == axis[..1]).Value;
        var turns = axis.EndsWith('-') ? -1 : 1;
        return WithSelected((s, part) => s.RotateAboutCenterAsync(part, MeshTransforms.Snap90(direction, turns)));
    }

    [RelayCommand]
    private Task Scale()
    {
        if (scaleFactor <= 0)
        {
            shell.ShowMessage("The scale factor must be greater than zero.", isError: true);
            return Task.CompletedTask;
        }

        return WithSelected((s, part) => s.RotateAboutCenterAsync(part, MeshTransforms.Scale(scaleFactor)), ScaleFactor);
    }

    [RelayCommand]
    private Task Place(StockAnchor anchor) => WithSelected((s, part) => s.PlaceOnStockAsync(part, anchor));

    [RelayCommand]
    private Task Undo() => WithSelected(async (s, part) =>
    {
        if (!await s.UndoTransformAsync(part))
        {
            shell.ShowMessage("Nothing to undo.");
        }
    });

    [RelayCommand]
    private async Task ClearTransforms()
    {
        if (Selected is { Transforms.Count: > 0 } part
            && await shell.ConfirmAsync("Reset model", $"Undo every move, rotation and scale of '{part.Name}'?", "Reset", danger: true))
        {
            await WithSelected((s, p) => s.ClearTransformsAsync(p));
        }
    }

    [RelayCommand]
    private async Task Suggest()
    {
        if (Selected is not { } part)
        {
            return;
        }

        var mode = Mode.Value;
        IReadOnlyList<OrientationCandidate> found = [];
        if (await workspace.EditAsync(async s => found = await s.SuggestOrientationsAsync(part, mode)))
        {
            Suggestions.Clear();
            foreach (var candidate in found)
            {
                Suggestions.Add(new OrientationRow(candidate, Length(candidate.Height)));
            }

            if (Suggestions.Count == 0)
            {
                shell.ShowMessage("No orientation suggestions for this model.");
            }
        }
    }

    [RelayCommand]
    private Task ApplySuggestion(OrientationRow row) => WithSelected((s, part) => s.ApplyOrientationAsync(part, row.Candidate));

    private Task WithSelected(Func<ProjectSession, PartModel, Task> action, params NumberField[] inputs)
    {
        if (Selected is not { } part)
        {
            shell.ShowMessage("Select a model first.");
            return Task.CompletedTask;
        }

        if (inputs.FirstOrDefault(f => f.HasError) is { } invalid)
        {
            shell.ShowMessage(invalid.Error!, isError: true);
            return Task.CompletedTask;
        }

        return EditGeometryAsync(s => action(s, part));
    }

    private async Task<bool> EditGeometryAsync(Func<ProjectSession, Task> action)
    {
        var ok = await workspace.EditAsync(action);
        await geometryChanged();
        return ok;
    }
}
