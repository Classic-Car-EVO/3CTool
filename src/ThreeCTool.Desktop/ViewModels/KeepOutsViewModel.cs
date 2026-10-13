using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Core.Projects;
using ThreeCTool.Desktop.Services;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>A keep-out zone in the list.</summary>
public sealed record KeepOutRow(KeepOutZone Zone, string Title, string Detail);

/// <summary>Clamps, fixtures and other solids the tool must stay out of.</summary>
public sealed partial class KeepOutsViewModel(IShell shell, ProjectWorkspace workspace, Func<Task> geometryChanged) : ObservableObject
{
    private SimpleSolid draft = new() { Shape = SolidShape.Box, SizeX = 20, SizeY = 20, Height = 20, Radius = 10 };

    public static IReadOnlyList<Choice<KeepOutAttachment>> Attachments { get; } =
    [
        new("Part (moves with the part, e.g. tabs)", KeepOutAttachment.Setup),
        new("Setup work coordinates (vise jaws, clamps)", KeepOutAttachment.SetupFixed),
        new("Machine (fixed to the table)", KeepOutAttachment.Machine),
    ];

    public static IReadOnlyList<Choice<SolidShape>> Shapes { get; } =
    [
        new("Box", SolidShape.Box),
        new("Cylinder (vertical)", SolidShape.Cylinder),
    ];

    public ObservableCollection<KeepOutRow> Zones { get; } = [];

    public ObservableCollection<Setup> Setups { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsSetup))]
    public partial Choice<KeepOutAttachment> Attachment { get; set; } = Attachments[1];

    public bool NeedsSetup => Attachment.Value != KeepOutAttachment.Machine;

    [ObservableProperty]
    public partial Setup? Setup { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBox))]
    public partial Choice<SolidShape> Shape { get; set; } = Shapes[0];

    public bool IsBox => Shape.Value == SolidShape.Box;

    [ObservableProperty]
    public partial IReadOnlyList<NumberField> PositionFields { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<NumberField> BoxFields { get; private set; } = [];

    [ObservableProperty]
    public partial NumberField? Radius { get; private set; }

    public void Load(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var unit = shell.Services.Settings.DisplayUnit;
        var keepSetup = Setup?.Id;
        Setups.Clear();
        foreach (var setup in session.Setups)
        {
            Setups.Add(setup);
        }

        Setup = Setups.FirstOrDefault(s => s.Id == keepSetup) ?? Setups.FirstOrDefault();
        Zones.Clear();
        foreach (var zone in session.KeepOuts)
        {
            Zones.Add(Row(zone, session.Setups, unit));
        }

        var d = draft;
        PositionFields =
        [
            NumberField.Length("Centre X", () => d.CenterX, v => d.CenterX = v, unit),
            NumberField.Length("Centre Y", () => d.CenterY, v => d.CenterY = v, unit),
            NumberField.Length("Bottom Z", () => d.BottomZ, v => d.BottomZ = v, unit),
            NumberField.PositiveLength("Height", () => d.Height, v => d.Height = v, unit),
        ];
        BoxFields =
        [
            NumberField.PositiveLength("Size X", () => d.SizeX, v => d.SizeX = v, unit),
            NumberField.PositiveLength("Size Y", () => d.SizeY, v => d.SizeY = v, unit),
        ];
        Radius = NumberField.PositiveLength("Radius", () => d.Radius, v => d.Radius = v, unit);
    }

    private static KeepOutRow Row(KeepOutZone zone, IReadOnlyList<Setup> setups, LengthUnit unit)
    {
        var s = zone.Solid;
        string L(double mm) => NumberField.FormatLength(mm, unit);
        var size = s.Shape == SolidShape.Box ? $"box {L(s.SizeX)} x {L(s.SizeY)} x {L(s.Height)}" : $"cylinder r {L(s.Radius)} x {L(s.Height)}";
        var where = zone.Attachment switch
        {
            KeepOutAttachment.Machine => "machine",
            KeepOutAttachment.Setup => "part",
            _ => "WCS",
        };
        var setup = zone.SetupId is { } id ? setups.FirstOrDefault(x => x.Id == id)?.Name ?? "missing setup" : null;
        var detail = $"{size} {NumberField.Abbreviation(unit)} at ({L(s.CenterX)}, {L(s.CenterY)}, {L(s.BottomZ)}), {where}"
            + (setup is null ? string.Empty : $", {setup}");
        return new KeepOutRow(zone, string.IsNullOrWhiteSpace(s.Name) ? "Keep-out" : s.Name, detail);
    }

    [RelayCommand]
    private async Task Add()
    {
        var fields = PositionFields.Concat(IsBox ? BoxFields : [Radius!]);
        if (fields.FirstOrDefault(f => f.HasError) is { } invalid)
        {
            shell.ShowMessage(invalid.Error!, isError: true);
            return;
        }

        var solid = EntityCopy.Clone(draft);
        solid.Shape = Shape.Value;
        solid.Name = string.IsNullOrWhiteSpace(Name) ? $"Keep-out {Zones.Count + 1}" : Name.Trim();
        var zone = new KeepOutZone
        {
            Attachment = Attachment.Value,
            SetupId = NeedsSetup ? Setup?.Id : null,
            Solid = solid,
        };
        if (await workspace.EditAsync(s => s.AddKeepOutAsync(zone)))
        {
            Name = string.Empty;
            draft = EntityCopy.Clone(solid);
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task Remove(KeepOutRow row)
    {
        if (await shell.ConfirmAsync("Remove keep-out zone", $"Remove '{row.Title}'?", "Remove", danger: true)
            && await workspace.EditAsync(s => s.RemoveKeepOutAsync(row.Zone)))
        {
            await ReloadAsync();
        }
    }

    private async Task ReloadAsync()
    {
        if (workspace.Session is { } session)
        {
            Load(session);
        }

        await geometryChanged();
    }
}
