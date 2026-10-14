using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core.Learning;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>A learned feed correction for a material/tool/strategy combination.</summary>
public sealed record CorrectionRow(FeedCorrection Correction, string Material, string Tool)
{
    public string Strategy => Correction.Strategy.ToString();

    public string ChipLoad => Correction.ChipLoadFactor.ToString("0.00×", CultureInfo.CurrentCulture);

    public string SurfaceSpeed => Correction.SurfaceSpeedFactor.ToString("0.00×", CultureInfo.CurrentCulture);

    public int Samples => Correction.SampleCount;

    public string Updated => Correction.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>Operator feedback recorded after a job.</summary>
public sealed record FeedbackRow(JobFeedback Feedback, string Material, string Tool)
{
    public string When => Feedback.RecordedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Project => Feedback.ProjectName;

    public string Ratings => string.Create(
        CultureInfo.CurrentCulture,
        $"finish {Rating(Feedback.FinishRating)}, chatter {Rating(Feedback.ChatterRating)}, wear {Rating(Feedback.WearRating)}");

    public string Overrides => string.Create(
        CultureInfo.CurrentCulture,
        $"{Feedback.FeedOverrides.Count} feed, {Feedback.RpmChanges.Count} RPM");

    public string Notes => Feedback.Notes ?? string.Empty;

    private static string Rating(int? value) => value?.ToString(CultureInfo.CurrentCulture) ?? "-";
}

/// <summary>Learned feed corrections (with reset) and the job feedback history they were learned from.</summary>
public sealed partial class LearningViewModel(IShell shell) : PageViewModel(shell, "Learning")
{
    public ObservableCollection<CorrectionRow> Corrections { get; } = [];

    public ObservableCollection<FeedbackRow> Feedback { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedCommand), nameof(ResetMaterialCommand), nameof(ResetToolCommand))]
    public partial CorrectionRow? SelectedCorrection { get; set; }

    public override Task ShowAsync() => Shell.RunAsync(async () =>
    {
        var library = Shell.Services.Library;
        var materials = (await library.ListMaterialsAsync()).ToDictionary(m => m.Id, m => m.Name);
        var tools = (await library.ListToolsAsync()).ToDictionary(t => t.Id, t => t.Name);
        static string Name(IReadOnlyDictionary<Guid, string> names, Guid? id) =>
            id is { } i ? names.TryGetValue(i, out var n) ? n : "(deleted)" : "-";

        var corrections = await library.ListCorrectionsAsync();
        var feedback = await library.ListFeedbackAsync();
        Corrections.Clear();
        foreach (var c in corrections.OrderBy(c => Name(materials, c.MaterialId), StringComparer.CurrentCulture).ThenBy(c => Name(tools, c.ToolId), StringComparer.CurrentCulture).ThenBy(c => c.Strategy))
        {
            Corrections.Add(new CorrectionRow(c, Name(materials, c.MaterialId), Name(tools, c.ToolId)));
        }

        Feedback.Clear();
        foreach (var f in feedback)
        {
            Feedback.Add(new FeedbackRow(f, Name(materials, f.MaterialId), Name(tools, f.ToolId)));
        }

        SelectedCorrection = null;
        ResetAllCommand.NotifyCanExecuteChanged();
    });

    private bool HasSelection() => SelectedCorrection is not null;

    private bool HasCorrections() => Corrections.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ResetSelectedAsync() => SelectedCorrection is { } row
        ? ResetAsync($"Reset the learned correction for {row.Material} / {row.Tool} / {row.Strategy}?", id: row.Correction.Id)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ResetMaterialAsync() => SelectedCorrection is { } row
        ? ResetAsync($"Reset all learned corrections for {row.Material}?", materialId: row.Correction.MaterialId)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ResetToolAsync() => SelectedCorrection is { } row
        ? ResetAsync($"Reset all learned corrections for {row.Tool}?", toolId: row.Correction.ToolId)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasCorrections))]
    private Task ResetAllAsync() => ResetAsync("Reset every learned feed correction? Feeds return to the material table values.");

    private async Task ResetAsync(string question, Guid? id = null, Guid? materialId = null, Guid? toolId = null)
    {
        if (!await Shell.ConfirmAsync("Reset corrections", question, "Reset", danger: true))
        {
            return;
        }

        var removed = 0;
        if (await Shell.RunAsync(async () => removed = await Shell.Services.Library.ResetCorrectionsAsync(id, materialId, toolId)))
        {
            Shell.ShowMessage(removed == 1 ? "Removed 1 correction." : $"Removed {removed} corrections.");
            await ShowAsync();
        }
    }
}
