namespace ThreeCTool.Desktop.Services;

/// <summary>Application-wide services shared by all pages; settings are loaded by <see cref="LoadAsync"/>.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices(AppPaths paths, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataDirectory);
        Paths = paths;
        Time = time ?? TimeProvider.System;
        Library = new LibraryService(paths.LibraryPath, Time);
        SettingsStore = new DesktopSettingsStore(paths.LibraryPath);
        Feeders = new PairedFeederStore(paths.LibraryPath, Time);
    }

    /// <summary>Raised after settings were saved (display unit, renderer and so on may have changed).</summary>
    public event EventHandler? SettingsChanged;

    public AppPaths Paths { get; }

    public TimeProvider Time { get; }

    public LibraryService Library { get; }

    public DesktopSettingsStore SettingsStore { get; }

    public PairedFeederStore Feeders { get; }

    public ComputeService Compute { get; } = new();

    public DesktopSettings Settings { get; private set; } = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Settings = await SettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await Compute.SetPreferredAsync(Settings.ComputeBackend, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores <paramref name="settings"/>, applies the compute backend and notifies the pages.</summary>
    public async Task SaveSettingsAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // No ConfigureAwait(false): handlers are view models and expect the caller's (UI) context.
        await SettingsStore.SaveAsync(settings, cancellationToken);
        Settings = settings;
        await Compute.SetPreferredAsync(settings.ComputeBackend, cancellationToken);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Compute.Dispose();
}
