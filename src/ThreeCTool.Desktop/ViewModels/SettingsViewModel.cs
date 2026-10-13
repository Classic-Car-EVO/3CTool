using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Compute;
using ThreeCTool.Core;
using ThreeCTool.Desktop.Services;
using ThreeCTool.Rendering;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>A labelled choice for a combo box.</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>Preferences (accelerator, renderer, units, folders) and diagnostics.</summary>
public sealed partial class SettingsViewModel(IShell shell) : PageViewModel(shell, "Settings")
{
    public static IReadOnlyList<Choice<ComputeBackend?>> Backends { get; } =
    [
        new("Automatic (CUDA, OpenCL, CPU)", null),
        new("CUDA", ComputeBackend.Cuda),
        new("OpenCL", ComputeBackend.OpenCL),
        new("CPU", ComputeBackend.Cpu),
    ];

    public static IReadOnlyList<Choice<PreviewRenderer>> Renderers { get; } =
    [
        new("Automatic (OpenGL, software fallback)", PreviewRenderer.Auto),
        new("OpenGL", PreviewRenderer.OpenGl),
        new("Software", PreviewRenderer.Software),
    ];

    public static IReadOnlyList<Choice<LengthUnit>> DisplayUnits { get; } =
    [
        new("Millimetres", LengthUnit.Millimeter),
        new("Centimetres", LengthUnit.Centimeter),
        new("Metres", LengthUnit.Meter),
        new("Inches", LengthUnit.Inch),
    ];

    [ObservableProperty]
    public partial Choice<ComputeBackend?> Backend { get; set; } = Backends[0];

    [ObservableProperty]
    public partial Choice<PreviewRenderer> Renderer { get; set; } = Renderers[0];

    [ObservableProperty]
    public partial Choice<LengthUnit> DisplayUnit { get; set; } = DisplayUnits[0];

    [ObservableProperty]
    public partial string ProjectsFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowRapids { get; set; } = true;

    /// <summary>Accelerators found by ILGPU.</summary>
    public ObservableCollection<string> Devices { get; } = [];

    [ObservableProperty]
    public partial string ActiveDevice { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? ComputeError { get; private set; }

    public string Diagnostics { get; } = string.Join(
        Environment.NewLine,
        $"3C Tool {typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev"}",
        $"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
        $"Library: {shell.Services.Paths.LibraryPath}",
        $"Data: {shell.Services.Paths.DataDirectory}");

    public override async Task ShowAsync()
    {
        Load(Shell.Services.Settings);
        RefreshCompute();
        var devices = await Task.Run(ComputeService.ListDevices);
        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(Describe(device));
        }

        if (Devices.Count == 0)
        {
            Devices.Add("No accelerators found.");
        }
    }

    private static string Describe(ComputeDeviceInfo device) =>
        string.Create(CultureInfo.CurrentCulture, $"{device.Backend}: {device.Name} ({device.MemoryBytes / (1024.0 * 1024 * 1024):0.#} GB)");

    private void Load(DesktopSettings settings)
    {
        Backend = Backends.First(b => b.Value == settings.ComputeBackend);
        Renderer = Renderers.First(r => r.Value == settings.Renderer);
        DisplayUnit = DisplayUnits.First(u => u.Value == settings.DisplayUnit);
        ProjectsFolder = settings.ProjectsFolder ?? AppPaths.DefaultProjectsFolder;
        ShowRapids = settings.ShowRapids;
    }

    private void RefreshCompute()
    {
        var compute = Shell.Services.Compute;
        ActiveDevice = compute.Device is { } device ? Describe(device) : "Not started (starts with the first computation).";
        ComputeError = compute.LastError;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var settings = Shell.Services.Settings.Clone();
        settings.ComputeBackend = Backend.Value;
        settings.Renderer = Renderer.Value;
        settings.DisplayUnit = DisplayUnit.Value;
        settings.ProjectsFolder = string.IsNullOrWhiteSpace(ProjectsFolder) || ProjectsFolder == AppPaths.DefaultProjectsFolder ? null : ProjectsFolder.Trim();
        settings.ShowRapids = ShowRapids;
        if (await Shell.RunAsync(() => Shell.Services.SaveSettingsAsync(settings)))
        {
            RefreshCompute();
            Shell.ShowMessage("Settings saved.");
        }
    }

    [RelayCommand]
    private void Revert() => Load(Shell.Services.Settings);

    [RelayCommand]
    private async Task BrowseProjectsFolderAsync()
    {
        if (await Shell.Files.PickFolderAsync("Projects folder") is { } folder)
        {
            ProjectsFolder = folder;
        }
    }

    /// <summary>Starts the accelerator now (normally lazy) to check that the selected backend works.</summary>
    [RelayCommand]
    private async Task TestAcceleratorAsync()
    {
        if (await Shell.RunAsync(async () =>
        {
            using var lease = await Shell.Services.Compute.AcquireAsync();
        }))
        {
            RefreshCompute();
            Shell.ShowMessage(ComputeError is null ? $"Accelerator ready: {ActiveDevice}" : ComputeError, ComputeError is not null);
        }
    }
}
