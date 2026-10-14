using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Core.Learning;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Desktop.Services;

namespace ThreeCTool.Desktop.ViewModels;

public sealed class ToolEditorViewModel : EditorViewModel<Tool>
{
    public ToolEditorViewModel(Tool tool, bool isNew, LengthUnit unit)
        : base(tool, isNew, unit)
    {
        Diameter = Length("Diameter", () => tool.Diameter, v => tool.Diameter = v);
        CornerRadius = Length("Corner radius", () => tool.CornerRadius, v => tool.CornerRadius = v);
        TipDiameter = Length("Tip diameter", () => tool.TipDiameter, v => tool.TipDiameter = v);
        TipAngle = Number("Tip angle (°)", () => tool.TipAngle, v => tool.TipAngle = v);
        TaperAngle = Number("Taper angle per side (°)", () => tool.TaperAngle, v => tool.TaperAngle = v);
        FluteCount = Track(NumberField.WholeNumber("Flutes", () => tool.FluteCount, v => tool.FluteCount = v));
        FluteLength = Length("Flute length", () => tool.FluteLength, v => tool.FluteLength = v);
        ShankDiameter = Length("Shank diameter", () => tool.ShankDiameter, v => tool.ShankDiameter = v);
        OverallLength = Length("Overall length", () => tool.OverallLength, v => tool.OverallLength = v);
        Stickout = Length("Stickout", () => tool.Stickout, v => tool.Stickout = v);
        HelixAngle = Number("Helix angle (°)", () => tool.HelixAngle, v => tool.HelixAngle = v);
        MaxDepthOfCut = Track(NumberField.OptionalLength("Max depth of cut", () => tool.MaxDepthOfCut, v => tool.MaxDepthOfCut = v, unit));
    }

    public static IReadOnlyList<ToolType> Types { get; } = Enum.GetValues<ToolType>();

    public static IReadOnlyList<ToolMaterial> Materials { get; } = Enum.GetValues<ToolMaterial>();

    public static IReadOnlyList<ToolStatus> Statuses { get; } = Enum.GetValues<ToolStatus>();

    public ToolType Type
    {
        get => Model.Type;
        set
        {
            if (Model.Type == value)
            {
                return;
            }

            Model.Type = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCornerRadius));
            OnPropertyChanged(nameof(ShowTipAngle));
            OnPropertyChanged(nameof(ShowTaper));
            OnPropertyChanged(nameof(ShowTipDiameter));
        }
    }

    public bool ShowCornerRadius => Model.Type == ToolType.BullNose;

    public bool ShowTipAngle => Model.Type is ToolType.VBit or ToolType.Chamfer;

    public bool ShowTaper => Model.Type == ToolType.TaperedBall;

    public bool ShowTipDiameter => Model.Type is ToolType.VBit or ToolType.TaperedBall;

    public NumberField Diameter { get; }

    public NumberField CornerRadius { get; }

    public NumberField TipDiameter { get; }

    public NumberField TipAngle { get; }

    public NumberField TaperAngle { get; }

    public NumberField FluteCount { get; }

    public NumberField FluteLength { get; }

    public NumberField ShankDiameter { get; }

    public NumberField OverallLength { get; }

    public NumberField Stickout { get; }

    public NumberField HelixAngle { get; }

    public NumberField MaxDepthOfCut { get; }
}

/// <summary>One entry of a tool's wear log.</summary>
public sealed record WearRow(string When, string Kind, string Usage, string Notes)
{
    public static WearRow From(ToolWearLog log) => new(
        log.LoggedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        log.Kind.ToString(),
        log.Kind == WearEventKind.Usage ? string.Create(CultureInfo.CurrentCulture, $"{log.RuntimeMinutes:0.#} min, {log.CutLength / 1000:0.##} m") : string.Empty,
        log.Notes ?? string.Empty);
}

public sealed partial class ToolsViewModel(IShell shell) : LibraryListViewModel<Tool, ToolEditorViewModel>(shell, "Tools")
{
    private IReadOnlyDictionary<Guid, ToolUsage> usage = new Dictionary<Guid, ToolUsage>();

    /// <summary>Runtime and cut length of the edited tool since its last replacement.</summary>
    [ObservableProperty]
    public partial string UsageText { get; private set; } = string.Empty;

    public ObservableCollection<WearRow> WearLog { get; } = [];

    /// <summary>Note stored with the next manual wear entry.</summary>
    [ObservableProperty]
    public partial string WearNotes { get; set; } = string.Empty;

    protected override Task<IReadOnlyList<Tool>> ListAsync() => Shell.Services.Library.ListToolsAsync();

    protected override async Task OnListedAsync() => usage = await Shell.Services.Library.ToolUsageAsync();

    protected override Task SaveItemAsync(Tool item) => Shell.Services.Library.SaveToolAsync(item);

    protected override Task DeleteItemAsync(Tool item) => Shell.Services.Library.DeleteToolAsync(item.Id);

    protected override Guid IdOf(Tool item) => item.Id;

    protected override string NameOf(Tool item) => item.Name;

    protected override Tool CreateNew() => new()
    {
        Name = "New tool",
        Type = ToolType.FlatEnd,
        Diameter = 6,
        ShankDiameter = 6,
        FluteCount = 2,
        FluteLength = 20,
        OverallLength = 50,
        Stickout = 25,
    };

    protected override Tool DuplicateItem(Tool item)
    {
        item.Id = Guid.CreateVersion7();
        item.Name += " (copy)";
        item.Status = ToolStatus.InStock;
        return item;
    }

    protected override ToolEditorViewModel CreateEditor(Tool item, bool isNew) => new(item, isNew, Unit);

    protected override void OnEditorReplaced()
    {
        LogWearCommand.NotifyCanExecuteChanged();
        _ = RefreshWearAsync();
    }

    private bool CanLogWear(WearEventKind kind) => Editor is { IsNew: false };

    /// <summary>Records a replacement, wear or breakage of the edited tool (updates its status).</summary>
    [RelayCommand(CanExecute = nameof(CanLogWear))]
    private async Task LogWearAsync(WearEventKind kind)
    {
        if (Editor is not { IsNew: false } editor)
        {
            return;
        }

        var id = editor.Model.Id;
        var notes = string.IsNullOrWhiteSpace(WearNotes) ? null : WearNotes.Trim();
        if (await Shell.RunAsync(() => Shell.Services.Library.LogWearAsync(id, kind, notes)))
        {
            WearNotes = string.Empty;
            if (editor.HasChanges)
            {
                // Keep the unsaved edits; only the status and log come from the library.
                var stored = (await Shell.Services.Library.ListToolsAsync()).FirstOrDefault(t => t.Id == id);
                editor.Model.Status = stored?.Status ?? editor.Model.Status;
                await RefreshWearAsync();
            }
            else
            {
                await ReloadAsync(id);
            }
        }
    }

    private async Task RefreshWearAsync()
    {
        WearLog.Clear();
        if (Editor is not { IsNew: false } editor)
        {
            UsageText = string.Empty;
            return;
        }

        var id = editor.Model.Id;
        IReadOnlyList<ToolWearLog> logs = [];
        if (!await Shell.RunAsync(async () =>
        {
            logs = await Shell.Services.Library.ListWearAsync(id);
            usage = await Shell.Services.Library.ToolUsageAsync();
        }) || Editor?.Model.Id != id)
        {
            return;
        }

        foreach (var log in logs)
        {
            WearLog.Add(WearRow.From(log));
        }

        UsageText = usage.TryGetValue(id, out var u)
            ? string.Create(CultureInfo.CurrentCulture, $"{u.RuntimeMinutes:0.#} min cutting, {u.CutLength / 1000:0.##} m cut length, {u.Runs} runs since {(u.LastReplaced is { } r ? "replacement on " + r.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : "first use")}")
            : "No recorded use.";
    }
}
