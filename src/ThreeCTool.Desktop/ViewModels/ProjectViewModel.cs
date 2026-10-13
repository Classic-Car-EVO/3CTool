using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Rendering;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>A recently opened project folder.</summary>
public sealed record RecentProject(string Folder)
{
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder));

    public bool Exists => ProjectSession.IsProjectFolder(Folder);
}

/// <summary>
/// The project page: a start screen (new, open, recent) without a project; otherwise the project's details, models,
/// stock and keep-out zones next to a 3D view.
/// </summary>
public sealed partial class ProjectViewModel : PageViewModel
{
    private readonly ProjectWorkspace workspace;

    public ProjectViewModel(IShell shell, ProjectWorkspace workspace)
        : base(shell, "Project")
    {
        this.workspace = workspace;
        Details = new ProjectDetailsViewModel(shell, workspace);
        Parts = new PartsViewModel(shell, workspace, RefreshGeometryAsync);
        Stock = new StockViewModel(shell, workspace, RefreshGeometryAsync);
        KeepOuts = new KeepOutsViewModel(shell, workspace, RefreshGeometryAsync);
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectWorkspace.Session))
            {
                OnPropertyChanged(nameof(HasProject));
                OnPropertyChanged(nameof(HasNoProject));
            }
        };
    }

    public static IReadOnlyList<Choice<Core.Projects.OptimizationGoal>> Goals => ProjectDetailsViewModel.Goals;

    public ProjectWorkspace Workspace => workspace;

    public bool HasProject => workspace.HasProject;

    public bool HasNoProject => !workspace.HasProject;

    public ProjectDetailsViewModel Details { get; }

    public PartsViewModel Parts { get; }

    public StockViewModel Stock { get; }

    public KeepOutsViewModel KeepOuts { get; }

    public ProjectSceneViewModel Scene { get; } = new();

    [ObservableProperty]
    public partial PreviewRenderer Renderer { get; private set; }

    // ----- Start screen -----

    public ObservableCollection<RecentProject> Recent { get; } = [];

    public ObservableCollection<MachineProfile> Machines { get; } = [];

    public ObservableCollection<Material> Materials { get; } = [];

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    /// <summary>Parent folder; the project goes into a sub-folder named after it.</summary>
    [ObservableProperty]
    public partial string NewLocation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial MachineProfile? NewMachine { get; set; }

    [ObservableProperty]
    public partial Material? NewMaterial { get; set; }

    [ObservableProperty]
    public partial Choice<Core.Projects.OptimizationGoal> NewGoal { get; set; } = ProjectDetailsViewModel.Goals[0];

    public override async Task ShowAsync()
    {
        Renderer = Shell.Services.Settings.Renderer;
        Scene.Scene.ShowRapids = Shell.Services.Settings.ShowRapids;
        if (workspace.HasProject)
        {
            await LoadProjectAsync();
            return;
        }

        await Shell.RunAsync(async () =>
        {
            var library = Shell.Services.Library;
            var machines = await library.ListMachinesAsync();
            var materials = await library.ListMaterialsAsync();
            Machines.Clear();
            foreach (var machine in machines)
            {
                Machines.Add(machine);
            }

            Materials.Clear();
            foreach (var material in materials)
            {
                Materials.Add(material);
            }

            NewMachine = Machines.FirstOrDefault(m => m.Id == NewMachine?.Id) ?? Machines.FirstOrDefault();
            NewMaterial = Materials.FirstOrDefault(m => m.Id == NewMaterial?.Id);
            if (string.IsNullOrEmpty(NewLocation))
            {
                NewLocation = Shell.Services.Settings.ProjectsFolder ?? AppPaths.DefaultProjectsFolder;
            }

            Recent.Clear();
            foreach (var folder in Shell.Services.Settings.RecentProjects)
            {
                Recent.Add(new RecentProject(folder));
            }
        });
    }

    [RelayCommand]
    private async Task BrowseLocation()
    {
        if (await Shell.Files.PickFolderAsync("Where to create the project") is { } folder)
        {
            NewLocation = folder;
        }
    }

    [RelayCommand]
    private async Task Create()
    {
        var name = NewName.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            Shell.ShowMessage("Enter a project name that can be used as a folder name.", isError: true);
            return;
        }

        if (NewMachine is not { } machine)
        {
            Shell.ShowMessage("Choose the machine (add one on the Library page first).", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(NewLocation))
        {
            Shell.ShowMessage("Choose where to create the project.", isError: true);
            return;
        }

        var folder = Path.Combine(NewLocation, name);
        var services = Shell.Services;
        var material = NewMaterial?.Id;
        var goal = NewGoal.Value;
        ProjectSession? session = null;
        if (await Shell.RunAsync(async () => session = await ProjectSession.CreateAsync(
            folder, services.Paths.LibraryPath, name, machine.Id, material, goal, services.Time)))
        {
            NewName = string.Empty;
            await AttachAsync(session!);
        }
    }

    [RelayCommand]
    private async Task Open()
    {
        if (await Shell.Files.PickFolderAsync("Open a project folder") is { } folder)
        {
            await OpenFolderAsync(folder);
        }
    }

    [RelayCommand]
    private async Task OpenRecent(RecentProject recent)
    {
        if (!recent.Exists)
        {
            if (await Shell.ConfirmAsync("Project not found", $"'{recent.Folder}' no longer contains a project. Remove it from the list?", "Remove"))
            {
                await RemoveRecent(recent);
            }

            return;
        }

        await OpenFolderAsync(recent.Folder);
    }

    [RelayCommand]
    private async Task RemoveRecent(RecentProject recent)
    {
        var settings = Shell.Services.Settings.Clone();
        settings.RecentProjects.RemoveAll(f => string.Equals(f, recent.Folder, StringComparison.Ordinal));
        if (await Shell.RunAsync(() => Shell.Services.SaveSettingsAsync(settings)))
        {
            Recent.Remove(recent);
        }
    }

    [RelayCommand]
    private async Task Close()
    {
        await workspace.ReplaceAsync(null);
        Scene.Clear();
        await ShowAsync();
    }

    public async Task OpenFolderAsync(string folder)
    {
        var services = Shell.Services;
        ProjectSession? session = null;
        if (await Shell.RunAsync(async () => session = await ProjectSession.OpenAsync(folder, services.Paths.LibraryPath, services.Time)))
        {
            await AttachAsync(session!);
        }
    }

    private async Task AttachAsync(ProjectSession session)
    {
        await workspace.ReplaceAsync(session);
        Scene.Clear();
        var settings = Shell.Services.Settings.Clone();
        DesktopSettingsStore.AddRecent(settings, session.Folder);
        await Shell.RunAsync(() => Shell.Services.SaveSettingsAsync(settings));
        await LoadProjectAsync();
        var changes = Details.LibraryChanges.Count;
        Shell.ShowMessage(changes == 0
            ? $"Opened '{session.Project.Name}'."
            : $"Opened '{session.Project.Name}'. {changes} library item(s) changed since the project copied them; see Overview.");
    }

    private async Task LoadProjectAsync()
    {
        await workspace.EditAsync(async s =>
        {
            await Details.LoadAsync(s);
            Stock.Load(s);
            KeepOuts.Load(s);
        });
        await RefreshGeometryAsync();
    }

    private async Task RefreshGeometryAsync()
    {
        await workspace.EditAsync(Scene.RefreshAsync);
        if (workspace.Session is { } session)
        {
            Parts.Load(session, Scene.PartBounds);
        }
    }
}
