using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using ThreeCTool.Core;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>A setup in the list; <see cref="Refresh"/> re-reads it after an edit.</summary>
public sealed partial class SetupRow : ObservableObject
{
    public SetupRow(Setup setup)
    {
        Setup = setup;
        Refresh();
    }

    public Setup Setup { get; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    public void Refresh()
    {
        Title = Setup.Name;
        var orientation = Setup.Orientation switch
        {
            SetupOrientation.Indexed => $"indexed A{Setup.IndexAngle.ToString("0.###", CultureInfo.InvariantCulture)}",
            SetupOrientation.SimultaneousRotary => "simultaneous rotary",
            _ => "3-axis",
        };
        var count = Setup.Operations.Count;
        Detail = $"{orientation}, {Setup.WorkOffset}, {count} operation{(count == 1 ? string.Empty : "s")}";
    }
}

/// <summary>An operation in the list with its toolpath and feed status (set by the page).</summary>
public sealed partial class OperationRow : ObservableObject
{
    public OperationRow(Operation operation, string toolName)
    {
        Operation = operation;
        Refresh(toolName);
    }

    public Operation Operation { get; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool Enabled { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = string.Empty;

    public bool HasStatus => Status.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string Warning { get; set; } = string.Empty;

    public bool HasWarning => Warning.Length > 0;

    public void Refresh(string toolName)
    {
        Title = Operation.Name;
        Enabled = Operation.Enabled;
        Detail = $"{ProjectSession.Describe(Operation.Strategy)}, {toolName}";
    }
}

/// <summary>Editor of a setup: orientation, work coordinates and probing. Every valid change is saved.</summary>
public sealed partial class SetupEditor : ObservableObject
{
    private readonly Func<Task> save;
    private readonly bool loading;

    public SetupEditor(Setup setup, MachineProfile machine, LengthUnit unit, Func<Task> save)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(machine);
        this.save = save;
        Setup = setup;
        loading = true;
        Name = setup.Name;
        RotaryCenters = [.. machine.RotaryAxis.Centers.Select(c => c.Name)];
        Orientation = Orientations.First(o => o.Value == setup.Orientation);
        RotaryCenter = setup.RotaryCenterName;
        WorkOffset = setup.WorkOffset;
        Reference = References.First(r => r.Value == setup.WcsReference);
        Probe = ProbeMethods.First(p => p.Value == setup.ProbeMethod);
        IndexAngle = Watch(NumberField.Number("Index angle (A, degrees)", () => setup.IndexAngle, v => setup.IndexAngle = v));
        OffsetFields =
        [
            Watch(NumberField.Length("WCS offset X", () => setup.WcsOffsetX, v => setup.WcsOffsetX = v, unit)),
            Watch(NumberField.Length("WCS offset Y", () => setup.WcsOffsetY, v => setup.WcsOffsetY = v, unit)),
            Watch(NumberField.Length("WCS offset Z", () => setup.WcsOffsetZ, v => setup.WcsOffsetZ = v, unit)),
        ];
        loading = false;
    }

    public static IReadOnlyList<Choice<SetupOrientation>> Orientations { get; } =
    [
        new("3-axis (A not used)", SetupOrientation.ThreeAxis),
        new("Indexed (A locked at an angle)", SetupOrientation.Indexed),
        new("Simultaneous rotary (A turns while cutting)", SetupOrientation.SimultaneousRotary),
    ];

    public static IReadOnlyList<WorkOffset> WorkOffsets { get; } = Enum.GetValues<WorkOffset>();

    public static IReadOnlyList<Choice<WcsReference>> References { get; } =
    [
        new("Stock top centre", WcsReference.StockTopCenter),
        new("Stock top front-left corner", WcsReference.StockTopFrontLeft),
        new("Part origin", WcsReference.PartOrigin),
        new("Rotary axis (centre line)", WcsReference.RotaryAxis),
    ];

    public static IReadOnlyList<Choice<ProbeMethod>> ProbeMethods { get; } =
    [
        new("Tool setter", ProbeMethod.ToolSetter),
        new("Touch plate", ProbeMethod.TouchPlate),
    ];

    public Setup Setup { get; }

    public IReadOnlyList<string> RotaryCenters { get; }

    public bool HasRotaryCenters => RotaryCenters.Count > 0;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRotary), nameof(IsIndexed))]
    public partial Choice<SetupOrientation> Orientation { get; set; }

    public bool IsRotary => Orientation.Value != SetupOrientation.ThreeAxis;

    public bool IsIndexed => Orientation.Value == SetupOrientation.Indexed;

    [ObservableProperty]
    public partial string? RotaryCenter { get; set; }

    [ObservableProperty]
    public partial WorkOffset WorkOffset { get; set; }

    [ObservableProperty]
    public partial Choice<WcsReference> Reference { get; set; }

    [ObservableProperty]
    public partial Choice<ProbeMethod> Probe { get; set; }

    public NumberField IndexAngle { get; }

    public IReadOnlyList<NumberField> OffsetFields { get; }

    partial void OnNameChanged(string value)
    {
        if (!loading && value.Trim() is { Length: > 0 } name && name != Setup.Name)
        {
            Setup.Name = name;
            _ = save();
        }
    }

    partial void OnOrientationChanged(Choice<SetupOrientation> value)
    {
        if (loading || value is null || value.Value == Setup.Orientation)
        {
            return;
        }

        // Rotary setups are zeroed on the A axis centre line, 3-axis ones on the stock.
        Setup.Orientation = value.Value;
        if (value.Value == SetupOrientation.ThreeAxis)
        {
            Setup.RotaryCenterName = null;
            if (Setup.WcsReference == WcsReference.RotaryAxis)
            {
                Setup.WcsReference = WcsReference.StockTopCenter;
            }
        }
        else
        {
            Setup.WcsReference = WcsReference.RotaryAxis;
            Setup.RotaryCenterName ??= RotaryCenters.FirstOrDefault();
        }

        Reference = References.First(r => r.Value == Setup.WcsReference);
        RotaryCenter = Setup.RotaryCenterName;
        _ = save();
    }

    partial void OnRotaryCenterChanged(string? value)
    {
        if (!loading && value is not null && value != Setup.RotaryCenterName)
        {
            Setup.RotaryCenterName = value;
            _ = save();
        }
    }

    partial void OnWorkOffsetChanged(WorkOffset value)
    {
        if (!loading && value != Setup.WorkOffset)
        {
            Setup.WorkOffset = value;
            _ = save();
        }
    }

    partial void OnReferenceChanged(Choice<WcsReference> value)
    {
        if (!loading && value is not null && value.Value != Setup.WcsReference)
        {
            Setup.WcsReference = value.Value;
            _ = save();
        }
    }

    partial void OnProbeChanged(Choice<ProbeMethod> value)
    {
        if (!loading && value is not null && value.Value != Setup.ProbeMethod)
        {
            Setup.ProbeMethod = value.Value;
            _ = save();
        }
    }

    private NumberField Watch(NumberField field)
    {
        field.Changed += (_, _) => _ = save();
        return field;
    }
}

/// <summary>
/// Editor of an operation: name, tool and strategy parameters. Empty optional fields use the defaults resolved from
/// the tool and the project tolerances, shown as placeholders. Every valid change is saved.
/// </summary>
public sealed partial class OperationEditor : ObservableObject
{
    private readonly Func<Task> save;
    private readonly Tolerances tolerances;
    private readonly LengthUnit unit;
    private readonly bool loading;

    public OperationEditor(Operation operation, IReadOnlyList<Tool> tools, Tolerances tolerances, LengthUnit unit, Func<Task> save)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(tools);
        this.save = save;
        this.tolerances = tolerances;
        this.unit = unit;
        Operation = operation;
        Tools = tools;
        loading = true;
        Name = operation.Name;
        Enabled = operation.Enabled;
        Tool = tools.FirstOrDefault(t => t.Id == operation.ToolId);
        var p = operation.Parameters;
        Direction = Directions.First(d => d.Value == p.Direction);
        Boundary = Boundaries.First(b => b.Value == p.Boundary);
        Linking = Linkings.First(l => l.Value == p.Linking);
        Entry = Entries.First(e => e.Value == p.Entry);
        Pattern = Patterns.First(x => x.Value == p.Pattern);
        RotaryPattern = RotaryPatterns.First(x => x.Value == p.RotaryPattern);
        Stepover = Watch(NumberField.OptionalLength("Stepover", () => p.Stepover, v => p.Stepover = v, unit));
        Stepdown = Watch(NumberField.OptionalLength("Stepdown", () => p.Stepdown, v => p.Stepdown = v, unit));
        Tolerance = Watch(NumberField.OptionalLength("Tolerance", () => p.Tolerance, v => p.Tolerance = v, unit));
        StockToLeave = Watch(NumberField.OptionalLength("Stock to leave (sides)", () => p.StockToLeave, v => p.StockToLeave = v, unit));
        AxialStockToLeave = Watch(NumberField.OptionalLength("Stock to leave (floors)", () => p.AxialStockToLeave, v => p.AxialStockToLeave = v, unit));
        RasterAngle = Watch(NumberField.Number("Raster angle (degrees)", () => p.RasterAngle, v => p.RasterAngle = v));
        BoundaryOffset = Watch(NumberField.Length("Boundary offset", () => p.BoundaryOffset, v => p.BoundaryOffset = v, unit));
        RampAngle = Watch(NumberField.Number("Ramp angle (degrees)", () => p.RampAngle, v => p.RampAngle = v));
        LeadRadius = Watch(NumberField.Length("Lead-in/out radius", () => p.LeadRadius, v => p.LeadRadius = v, unit));
        MinZ = Watch(NumberField.OptionalLength("Lowest Z", () => p.MinZ, v => p.MinZ = v, unit));
        MaxZ = Watch(NumberField.OptionalLength("Highest Z", () => p.MaxZ, v => p.MaxZ = v, unit));
        PencilPasses = Watch(NumberField.WholeNumber("Passes per side", () => p.PencilPasses, v => p.PencilPasses = v));
        RetractClearance = Watch(NumberField.Length("Retract clearance", () => p.RetractClearance, v => p.RetractClearance = v, unit));
        MinZ.Hint = "bottom of the part";
        MaxZ.Hint = "top of the stock";
        UpdateHints();
        loading = false;
    }

    public static IReadOnlyList<Choice<CutDirection>> Directions { get; } =
    [
        new("Climb", CutDirection.Climb),
        new("Conventional", CutDirection.Conventional),
        new("Both (zig-zag)", CutDirection.Both),
    ];

    public static IReadOnlyList<Choice<BoundaryKind>> Boundaries { get; } =
    [
        new("Stock outline", BoundaryKind.Stock),
        new("Part silhouette", BoundaryKind.PartSilhouette),
    ];

    public static IReadOnlyList<Choice<LinkingMode>> Linkings { get; } =
    [
        new("Stay down where safe", LinkingMode.StayDown),
        new("Always retract", LinkingMode.Retract),
    ];

    public static IReadOnlyList<Choice<EntryMode>> Entries { get; } =
    [
        new("Helix", EntryMode.Helix),
        new("Ramp", EntryMode.Ramp),
        new("Plunge", EntryMode.Plunge),
    ];

    public static IReadOnlyList<Choice<ClearingPattern>> Patterns { get; } =
    [
        new("Offset (follows the outline)", ClearingPattern.Offset),
        new("Raster (parallel lines)", ClearingPattern.Raster),
    ];

    public static IReadOnlyList<Choice<RotaryPattern>> RotaryPatterns { get; } =
    [
        new("Spiral", Core.Projects.RotaryPattern.Spiral),
        new("Rings", Core.Projects.RotaryPattern.Rings),
        new("Lines along X", Core.Projects.RotaryPattern.Lines),
    ];

    public Operation Operation { get; }

    public IReadOnlyList<Tool> Tools { get; }

    public string StrategyText => ProjectSession.Describe(Operation.Strategy);

    public bool IsRotary => Operation.Strategy is StrategyType.RotaryRoughing or StrategyType.RotaryFinishing;

    public bool IsPlanar => !IsRotary;

    public bool UsesEntry => ToolpathSettings.IsRoughing(Operation.Strategy);

    public bool UsesPattern => Operation.Strategy is StrategyType.ZLevelRoughing or StrategyType.RestMachining;

    public bool UsesRasterAngle => Operation.Strategy is StrategyType.ParallelFinishing or StrategyType.ZLevelRoughing or StrategyType.RestMachining;

    public bool IsPencil => Operation.Strategy == StrategyType.Pencil;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial Tool? Tool { get; set; }

    [ObservableProperty]
    public partial Choice<CutDirection> Direction { get; set; }

    [ObservableProperty]
    public partial Choice<BoundaryKind> Boundary { get; set; }

    [ObservableProperty]
    public partial Choice<LinkingMode> Linking { get; set; }

    [ObservableProperty]
    public partial Choice<EntryMode> Entry { get; set; }

    [ObservableProperty]
    public partial Choice<ClearingPattern> Pattern { get; set; }

    [ObservableProperty]
    public partial Choice<RotaryPattern> RotaryPattern { get; set; }

    public NumberField Stepover { get; }

    public NumberField Stepdown { get; }

    public NumberField Tolerance { get; }

    public NumberField StockToLeave { get; }

    public NumberField AxialStockToLeave { get; }

    public NumberField RasterAngle { get; }

    public NumberField BoundaryOffset { get; }

    public NumberField RampAngle { get; }

    public NumberField LeadRadius { get; }

    public NumberField MinZ { get; }

    public NumberField MaxZ { get; }

    public NumberField PencilPasses { get; }

    public NumberField RetractClearance { get; }

    /// <summary>Fields with a parse error, in display order.</summary>
    public IEnumerable<NumberField> Invalid =>
        new[] { Stepover, Stepdown, Tolerance, StockToLeave, AxialStockToLeave, RasterAngle, BoundaryOffset, RampAngle, LeadRadius, MinZ, MaxZ, PencilPasses, RetractClearance }
            .Where(f => f.HasError);

    /// <summary>Shows the values the empty optional fields resolve to with the chosen tool.</summary>
    private void UpdateHints()
    {
        if (Tool is not { } tool)
        {
            return;
        }

        var resolved = ToolpathSettings.Resolve(Operation, tool, tolerances);
        string Auto(double mm) => "auto: " + NumberField.FormatLength(mm, unit);
        Stepover.Hint = Auto(resolved.Stepover);
        Stepdown.Hint = Auto(resolved.Stepdown);
        Tolerance.Hint = Auto(resolved.Tolerance);
        StockToLeave.Hint = Auto(resolved.RadialStock);
        AxialStockToLeave.Hint = Auto(resolved.AxialStock);
    }

    private void Changed()
    {
        UpdateHints();
        _ = save();
    }

    partial void OnNameChanged(string value)
    {
        if (!loading && value.Trim() is { Length: > 0 } name && name != Operation.Name)
        {
            Operation.Name = name;
            _ = save();
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!loading && value != Operation.Enabled)
        {
            Operation.Enabled = value;
            _ = save();
        }
    }

    partial void OnToolChanged(Tool? value)
    {
        if (!loading && value is not null && value.Id != Operation.ToolId)
        {
            Operation.ToolId = value.Id;
            Operation.Conditions = null;
            Changed();
        }
    }

    partial void OnDirectionChanged(Choice<CutDirection> value) => Set(value, p => p.Direction, (p, v) => p.Direction = v);

    partial void OnBoundaryChanged(Choice<BoundaryKind> value) => Set(value, p => p.Boundary, (p, v) => p.Boundary = v);

    partial void OnLinkingChanged(Choice<LinkingMode> value) => Set(value, p => p.Linking, (p, v) => p.Linking = v);

    partial void OnEntryChanged(Choice<EntryMode> value) => Set(value, p => p.Entry, (p, v) => p.Entry = v);

    partial void OnPatternChanged(Choice<ClearingPattern> value) => Set(value, p => p.Pattern, (p, v) => p.Pattern = v);

    partial void OnRotaryPatternChanged(Choice<RotaryPattern> value) => Set(value, p => p.RotaryPattern, (p, v) => p.RotaryPattern = v);

    private void Set<T>(Choice<T>? choice, Func<OperationParameters, T> get, Action<OperationParameters, T> set)
    {
        if (loading || choice is null || EqualityComparer<T>.Default.Equals(get(Operation.Parameters), choice.Value))
        {
            return;
        }

        set(Operation.Parameters, choice.Value);
        Changed();
    }

    private NumberField Watch(NumberField field)
    {
        field.Changed += (_, _) => Changed();
        return field;
    }
}
