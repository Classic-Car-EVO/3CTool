using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Core.Machines;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Streaming.Config;

namespace ThreeCTool.Desktop.ViewModels;

public sealed class ColletRow : ViewModelBase
{
    public ColletRow(Collet collet, LengthUnit unit)
    {
        Collet = collet;
        Shank = NumberField.Length("Shank", () => collet.ShankDiameter, v => collet.ShankDiameter = v, unit);
        Holder = new TableField(
            "Holder (mm)",
            "diameter x length; ...",
            'x',
            () => collet.Holder.Select(h => (h.Diameter, h.Length)),
            pairs => collet.Holder = [.. pairs.Select(p => new HolderSegment { Diameter = p.A, Length = p.B })]);
    }

    public Collet Collet { get; }

    public NumberField Shank { get; }

    /// <summary>Holder outline from the nut face upward, as diameter x length steps.</summary>
    public TableField Holder { get; }

    public IEnumerable<IEditField> Fields => [Shank, Holder];
}

public sealed class RotaryCenterRow : ViewModelBase
{
    public RotaryCenterRow(RotaryCenter center, LengthUnit unit)
    {
        Center = center;
        Height = NumberField.Length("Centre height", () => center.CenterHeight, v => center.CenterHeight = v, unit);
        Y = NumberField.Length("Machine Y", () => center.CenterY, v => center.CenterY = v, unit);
        Z = NumberField.Length("Machine Z", () => center.CenterZ, v => center.CenterZ = v, unit);
    }

    public RotaryCenter Center { get; }

    public NumberField Height { get; }

    public NumberField Y { get; }

    public NumberField Z { get; }

    public IEnumerable<IEditField> Fields => [Height, Y, Z];
}

/// <summary>A controller-owned axis setting (read-only; imported from FluidNC).</summary>
public sealed record AxisRow(string Axis, string StepsPerUnit, string MaxRate, string Acceleration, string Travel, string Homing)
{
    public static AxisRow From(AxisConfig axis, LengthUnit unit)
    {
        var rotary = axis.Axis is AxisName.A or AxisName.B;
        string Linear(double mm) => rotary
            ? mm.ToString("0.###", CultureInfo.InvariantCulture) + "°"
            : NumberField.FormatLength(mm, unit) + " " + NumberField.Abbreviation(unit);
        var perUnit = rotary ? "°" : "mm";
        return new AxisRow(
            axis.Axis.ToString(),
            string.Create(CultureInfo.InvariantCulture, $"{axis.StepsPerUnit:0.###}/{perUnit}"),
            string.Create(CultureInfo.InvariantCulture, $"{axis.MaxRatePerMinute:0} {perUnit}/min"),
            string.Create(CultureInfo.InvariantCulture, $"{axis.AccelerationPerSecond2:0.#} {perUnit}/s²"),
            Linear(axis.MaxTravel) + (axis.SoftLimits ? " (soft limits)" : string.Empty),
            axis.HomingCycle == 0 ? "not homed" : string.Create(CultureInfo.InvariantCulture, $"cycle {axis.HomingCycle}, {(axis.HomingPositiveDirection ? "+" : "-")}, at {axis.HomingMachinePosition:0.###}"));
    }
}

public sealed partial class MachineEditorViewModel : EditorViewModel<MachineProfile>
{
    public MachineEditorViewModel(MachineProfile machine, bool isNew, LengthUnit unit)
        : base(machine, isNew, unit)
    {
        SafeZ = Length("Safe Z (machine)", () => machine.SafeZMachine, v => machine.SafeZMachine = v);
        ArcTolerance = Length("Arc fitting tolerance", () => machine.ArcFittingTolerance, v => machine.ArcFittingTolerance = v);

        MinRpm = Number("Minimum RPM", () => machine.Spindle.MinRpm, v => machine.Spindle.MinRpm = v);
        MaxRpm = Number("Maximum RPM", () => machine.Spindle.MaxRpm, v => machine.Spindle.MaxRpm = v);
        RatedPower = Number("Rated power (W)", () => machine.Spindle.RatedPowerW, v => machine.Spindle.RatedPowerW = v);
        RatedRpm = Number("Rated RPM", () => machine.Spindle.RatedRpm, v => machine.Spindle.RatedRpm = v);
        PowerCurve = Track(new TableField(
            "Power curve",
            "rpm=watts; ... (empty = constant torque)",
            '=',
            () => machine.Spindle.PowerCurve.Select(p => (p.Rpm, p.PowerW)),
            pairs => machine.Spindle.PowerCurve = [.. pairs.Select(p => new PowerCurvePoint { Rpm = p.A, PowerW = p.B })]));

        MaxDiameter = Length("Maximum diameter", () => machine.RotaryAxis.MaxDiameter, v => machine.RotaryAxis.MaxDiameter = v);
        MaxLength = Length("Maximum length", () => machine.RotaryAxis.MaxLength, v => machine.RotaryAxis.MaxLength = v);
        MinCenterHeight = Length("Minimum centre height", () => machine.RotaryAxis.MinCenterHeight, v => machine.RotaryAxis.MinCenterHeight = v);
        MaxCenterHeight = Length("Maximum centre height", () => machine.RotaryAxis.MaxCenterHeight, v => machine.RotaryAxis.MaxCenterHeight = v);

        ToolSetterX = Length("Tool setter X", () => machine.Probe.ToolSetterX, v => machine.Probe.ToolSetterX = v);
        ToolSetterY = Length("Tool setter Y", () => machine.Probe.ToolSetterY, v => machine.Probe.ToolSetterY = v);
        ToolSetterStartZ = Length("Tool setter start Z", () => machine.Probe.ToolSetterStartZ, v => machine.Probe.ToolSetterStartZ = v);
        TouchPlate = Length("Touch plate thickness", () => machine.Probe.TouchPlateThickness, v => machine.Probe.TouchPlateThickness = v);
        ProbeDistance = Length("Maximum probe travel", () => machine.Probe.MaxDistance, v => machine.Probe.MaxDistance = v);
        ProbeRetract = Length("Probe retract", () => machine.Probe.Retract, v => machine.Probe.Retract = v);
        SeekFeed = Number("Seek feed (mm/min)", () => machine.Probe.SeekFeed, v => machine.Probe.SeekFeed = v);
        LatchFeed = Number("Latch feed (mm/min)", () => machine.Probe.LatchFeed, v => machine.Probe.LatchFeed = v);

        BaudRate = Track(NumberField.WholeNumber("Baud rate", () => machine.Connection.BaudRate, v => machine.Connection.BaudRate = v));
        TcpPort = Track(NumberField.WholeNumber("TCP port", () => machine.Connection.TcpPort, v => machine.Connection.TcpPort = v));

        Collets = [.. machine.Collets.Select(c => new ColletRow(c, unit))];
        Centers = [.. machine.RotaryAxis.Centers.Select(c => new RotaryCenterRow(c, unit))];
        RefreshController();
    }

    public static IReadOnlyList<TransportKind> Transports { get; } = Enum.GetValues<TransportKind>();

    public static IReadOnlyList<ProbeMethod> ProbeMethods { get; } = Enum.GetValues<ProbeMethod>();

    public static IReadOnlyList<CoolantMode> CoolantModes { get; } = Enum.GetValues<CoolantMode>();

    public NumberField SafeZ { get; }

    public NumberField ArcTolerance { get; }

    public NumberField MinRpm { get; }

    public NumberField MaxRpm { get; }

    public NumberField RatedPower { get; }

    public NumberField RatedRpm { get; }

    public TableField PowerCurve { get; }

    public NumberField MaxDiameter { get; }

    public NumberField MaxLength { get; }

    public NumberField MinCenterHeight { get; }

    public NumberField MaxCenterHeight { get; }

    public NumberField ToolSetterX { get; }

    public NumberField ToolSetterY { get; }

    public NumberField ToolSetterStartZ { get; }

    public NumberField TouchPlate { get; }

    public NumberField ProbeDistance { get; }

    public NumberField ProbeRetract { get; }

    public NumberField SeekFeed { get; }

    public NumberField LatchFeed { get; }

    public NumberField BaudRate { get; }

    public NumberField TcpPort { get; }

    public ObservableCollection<ColletRow> Collets { get; }

    public ObservableCollection<RotaryCenterRow> Centers { get; }

    /// <summary>Controller-owned axis settings.</summary>
    public ObservableCollection<AxisRow> Axes { get; } = [];

    /// <summary>Settings that changed with the last config import.</summary>
    public ObservableCollection<string> ImportChanges { get; } = [];

    public string ControllerSummary { get; private set; } = string.Empty;

    public bool HasNoAxes => Model.Axes.Count == 0;

    /// <summary>Imports controller-owned settings from a FluidNC config and lists what changed.</summary>
    public void ApplyImport(ControllerConfig config, DateTimeOffset importedAt)
    {
        ArgumentNullException.ThrowIfNull(config);
        var changes = Model.Axes.Count > 0 ? ProfileImport.Compare(Model, config) : [];
        ProfileImport.Apply(Model, config, null, importedAt);
        SafeZ.Refresh();
        ImportChanges.Clear();
        foreach (var change in changes)
        {
            ImportChanges.Add(change.ToString());
        }

        RefreshController();
    }

    protected override IEnumerable<IEditField> AllFields() =>
        base.AllFields().Concat(Collets.SelectMany(c => c.Fields)).Concat(Centers.SelectMany(c => c.Fields));

    [RelayCommand]
    private void AddCollet()
    {
        var collet = new Collet { Name = "ER collet", MachineProfileId = Model.Id, ShankDiameter = 6 };
        Model.Collets.Add(collet);
        Collets.Add(new ColletRow(collet, Unit));
    }

    [RelayCommand]
    private void RemoveCollet(ColletRow row)
    {
        Model.Collets.Remove(row.Collet);
        Collets.Remove(row);
    }

    [RelayCommand]
    private void AddCenter()
    {
        var center = new RotaryCenter { Name = $"Position {Centers.Count + 1}", CenterHeight = Model.RotaryAxis.MinCenterHeight };
        Model.RotaryAxis.Centers.Add(center);
        Centers.Add(new RotaryCenterRow(center, Unit));
    }

    [RelayCommand]
    private void RemoveCenter(RotaryCenterRow row)
    {
        Model.RotaryAxis.Centers.Remove(row.Center);
        Centers.Remove(row);
    }

    private void RefreshController()
    {
        Axes.Clear();
        foreach (var axis in Model.Axes.OrderBy(a => a.Axis))
        {
            Axes.Add(AxisRow.From(axis, Unit));
        }

        var controller = Model.Controller;
        ControllerSummary = controller.ConfigImportedAt is { } at
            ? string.Create(CultureInfo.CurrentCulture, $"Imported {at.ToLocalTime():g}{(controller.FluidNcVersion is { } v ? $", FluidNC {v}" : string.Empty)}, config {controller.ConfigHash?[..Math.Min(12, controller.ConfigHash.Length)]}; junction deviation {Model.JunctionDeviation:0.###} mm, {Model.PlannerBlocks} planner blocks")
            : "Not imported: import the FluidNC config.yaml (or connect to the controller) to fill in the axes.";
        OnPropertyChanged(nameof(ControllerSummary));
        OnPropertyChanged(nameof(HasNoAxes));
    }
}

public sealed partial class MachinesViewModel(IShell shell) : LibraryListViewModel<MachineProfile, MachineEditorViewModel>(shell, "Machines")
{
    public static IReadOnlyList<FileType> ConfigTypes { get; } = [new("FluidNC config", ["*.yaml", "*.yml", "*.txt"]), FileType.Any];

    protected override Task<IReadOnlyList<MachineProfile>> ListAsync() => Shell.Services.Library.ListMachinesAsync();

    protected override Task SaveItemAsync(MachineProfile item) => Shell.Services.Library.SaveMachineAsync(item);

    protected override Task DeleteItemAsync(MachineProfile item) => Shell.Services.Library.DeleteMachineAsync(item.Id);

    protected override Guid IdOf(MachineProfile item) => item.Id;

    protected override string NameOf(MachineProfile item) => item.Name;

    protected override MachineProfile CreateNew() => new() { Name = "New machine" };

    protected override MachineProfile DuplicateItem(MachineProfile item) => LibraryService.Duplicate(item, item.Name + " (copy)");

    protected override MachineEditorViewModel CreateEditor(MachineProfile item, bool isNew) => new(item, isNew, Unit);

    protected override string? DeleteBlocker(MachineProfile item) => Items.Count <= 1 ? "The library needs at least one machine." : null;

    protected override void OnEditorReplaced() => ImportConfigCommand.NotifyCanExecuteChanged();

    private bool CanImport() => Editor is not null;

    /// <summary>Reads a FluidNC config file (<c>config.yaml</c> or a <c>$Config/Dump</c> capture) into the edited machine.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportConfigAsync()
    {
        if (Editor is not { } editor || await Shell.Files.OpenFileAsync("Import FluidNC config", ConfigTypes) is not { } path)
        {
            return;
        }

        await Shell.RunAsync(async () =>
        {
            var config = ControllerConfig.Parse(await File.ReadAllLinesAsync(path));
            if (config.Axes().Count == 0)
            {
                throw new LibraryValidationException($"No axes found in {Path.GetFileName(path)}; is it a FluidNC config?");
            }

            editor.ApplyImport(config, Shell.Services.Time.GetUtcNow());
            Shell.ShowMessage(editor.ImportChanges.Count > 0
                ? $"Imported {config.Axes().Count} axes; {editor.ImportChanges.Count} settings changed. Save to keep them."
                : $"Imported {config.Axes().Count} axes. Save to keep them.");
        });
    }
}
