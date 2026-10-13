using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Data;
using ThreeCTool.Desktop.Services;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>A library tool offered for adding to the project.</summary>
public sealed partial class ToolPick(Tool tool) : ObservableObject
{
    public Tool Tool { get; } = tool;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A library entity that changed (or disappeared) since the project copied it.</summary>
public sealed record SnapshotRow(SnapshotDifference Difference)
{
    public string Text => Difference.Kind == SnapshotDifferenceKind.ChangedInLibrary
        ? $"{Kind}: '{Difference.Name}' was changed in the library."
        : $"{Kind}: '{Difference.Name}' is no longer in the library; the project keeps its copy.";

    public bool CanRefresh => Difference.Kind == SnapshotDifferenceKind.ChangedInLibrary;

    private string Kind => Difference.EntityType switch
    {
        nameof(MachineProfile) => "Machine",
        nameof(Material) => "Material",
        _ => Difference.EntityType,
    };
}

/// <summary>Project name, goal, tolerances, machine, material and tools; changes are saved immediately.</summary>
public sealed partial class ProjectDetailsViewModel(IShell shell, ProjectWorkspace workspace) : ObservableObject
{
    private bool loading;

    public static IReadOnlyList<Choice<OptimizationGoal>> Goals { get; } =
    [
        new("Best finish", OptimizationGoal.Quality),
        new("Shortest run time", OptimizationGoal.Speed),
        new("Longest tool life", OptimizationGoal.ToolLife),
    ];

    public ObservableCollection<MachineProfile> Machines { get; } = [];

    public ObservableCollection<Material> Materials { get; } = [];

    public ObservableCollection<Tool> ProjectTools { get; } = [];

    public ObservableCollection<ToolPick> AvailableTools { get; } = [];

    public ObservableCollection<SnapshotRow> LibraryChanges { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<NumberField> Tolerances { get; private set; } = [];

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NameError { get; private set; }

    [ObservableProperty]
    public partial Choice<OptimizationGoal> Goal { get; set; } = Goals[0];

    [ObservableProperty]
    public partial MachineProfile? Machine { get; set; }

    [ObservableProperty]
    public partial Material? Material { get; set; }

    /// <summary>Reset the tolerances to the new material's scale when the material changes.</summary>
    [ObservableProperty]
    public partial bool RescaleTolerances { get; set; } = true;

    /// <summary>Machine and material of the project (its snapshots, which may differ from the library).</summary>
    [ObservableProperty]
    public partial string SnapshotSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasLibraryChanges { get; private set; }

    /// <summary>Re-reads everything from the project and the library.</summary>
    public async Task LoadAsync(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var library = shell.Services.Library;
        var machines = await library.ListMachinesAsync();
        var materials = await library.ListMaterialsAsync();
        var tools = await library.ListToolsAsync();
        var changes = await session.CompareWithLibraryAsync();
        loading = true;
        try
        {
            var unit = shell.Services.Settings.DisplayUnit;
            var project = session.Project;
            Name = project.Name;
            NameError = null;
            Goal = Goals.First(g => g.Value == project.Goal);
            Replace(Machines, machines);
            Machine = machines.FirstOrDefault(m => m.Id == project.MachineProfileId);
            Replace(Materials, materials);
            Material = materials.FirstOrDefault(m => m.Id == project.MaterialId);
            Replace(ProjectTools, session.Tools.OrderBy(t => t.Diameter));
            Replace(AvailableTools, tools.Where(t => session.FindTool(t.Id) is null).Select(t => new ToolPick(t)));
            Replace(LibraryChanges, changes.Select(c => new SnapshotRow(c)));
            HasLibraryChanges = LibraryChanges.Count > 0;
            SnapshotSummary = $"Project copy of the machine: {session.Machine.Name}. Material: {session.Material?.Name ?? "not chosen"}.";
            Tolerances = TolerancesFields(project.Tolerances, unit);
        }
        finally
        {
            loading = false;
        }
    }

    private List<NumberField> TolerancesFields(Tolerances t, LengthUnit unit)
    {
        List<NumberField> fields =
        [
            NumberField.PositiveLength("Roughing tolerance", () => t.RoughingTolerance, v => t.RoughingTolerance = v, unit),
            NumberField.PositiveLength("Finishing tolerance", () => t.FinishingTolerance, v => t.FinishingTolerance = v, unit),
            NumberField.PositiveLength("Arc fitting tolerance", () => t.ArcFittingTolerance, v => t.ArcFittingTolerance = v, unit),
            NumberField.Length("Roughing stock to leave", () => t.RoughingStockToLeave, v => t.RoughingStockToLeave = v, unit),
            NumberField.Length("Semi-finish stock to leave", () => t.SemiFinishStockToLeave, v => t.SemiFinishStockToLeave = v, unit),
            NumberField.Length("Finishing stock to leave", () => t.FinishingStockToLeave, v => t.FinishingStockToLeave = v, unit),
            NumberField.PositiveLength("Max scallop height", () => t.MaxScallopHeight, v => t.MaxScallopHeight = v, unit),
            NumberField.PositiveLength("Gouge threshold", () => t.GougeThreshold, v => t.GougeThreshold = v, unit),
        ];
        foreach (var field in fields)
        {
            field.Changed += (_, _) => _ = workspace.EditAsync(s => s.SaveAsync());
        }

        return fields;
    }

    partial void OnNameChanged(string value)
    {
        if (loading || !workspace.HasProject)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            NameError = "The project needs a name.";
            return;
        }

        NameError = null;
        var name = value.Trim();
        _ = workspace.EditAsync(s =>
        {
            s.Project.Name = name;
            return s.SaveAsync();
        });
    }

    partial void OnGoalChanged(Choice<OptimizationGoal> value)
    {
        if (loading || !workspace.HasProject || value is null)
        {
            return;
        }

        var goal = value.Value;
        _ = workspace.EditAsync(s =>
        {
            s.Project.Goal = goal;
            return s.SaveAsync();
        });
    }

    partial void OnMachineChanged(MachineProfile? value)
    {
        if (loading || value is null || workspace.Session is not { } session || session.Project.MachineProfileId == value.Id)
        {
            return;
        }

        _ = ChangeAsync(s => s.SetMachineAsync(value.Id));
    }

    partial void OnMaterialChanged(Material? value)
    {
        if (loading || value is null || workspace.Session is not { } session || session.Project.MaterialId == value.Id)
        {
            return;
        }

        var rescale = RescaleTolerances;
        _ = ChangeAsync(s => s.SetMaterialAsync(value.Id, rescale));
    }

    [RelayCommand]
    private async Task AddTools()
    {
        var ids = AvailableTools.Where(t => t.IsSelected).Select(t => t.Tool.Id).ToList();
        if (ids.Count == 0)
        {
            shell.ShowMessage("Tick the tools to add first.");
            return;
        }

        await ChangeAsync(s => s.AddToolsAsync(ids));
    }

    [RelayCommand]
    private Task RemoveTool(Tool tool) => ChangeAsync(s => s.RemoveToolAsync(tool.Id));

    [RelayCommand]
    private Task RefreshSnapshot(SnapshotRow row) => ChangeAsync(s => s.RefreshSnapshotAsync(row.Difference));

    [RelayCommand]
    private Task RefreshAllSnapshots() => ChangeAsync(async s =>
    {
        foreach (var row in LibraryChanges.Where(r => r.CanRefresh).ToList())
        {
            await s.RefreshSnapshotAsync(row.Difference);
        }
    });

    /// <summary>Applies a change and re-reads the page (snapshots, tolerances and lists may all change).</summary>
    private async Task ChangeAsync(Func<ProjectSession, Task> change)
    {
        await workspace.EditAsync(change);
        await workspace.EditAsync(LoadAsync);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
