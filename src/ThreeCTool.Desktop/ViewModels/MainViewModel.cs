using System.Data.Common;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.EntityFrameworkCore;

using ThreeCTool.Desktop.Services;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>The main window: page navigation, status messages, confirmations and error handling for all pages.</summary>
public sealed partial class MainViewModel : ViewModelBase, IShell, IDisposable
{
    private readonly bool ready;
    private int busy;

    public MainViewModel(AppServices services, IFileDialogs files)
    {
        Services = services;
        Files = files;
        Workspace = new ProjectWorkspace(this);
        Project = new ProjectViewModel(this, Workspace);
        Machining = new MachiningViewModel(this, Workspace);
        Library = new LibraryViewModel(this);
        Settings = new SettingsViewModel(this);
        Pages = [Project, Machining, Library, Settings];
        SelectedPage = Project;
        Workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectWorkspace.Session))
            {
                if (Workspace.Session is { } session)
                {
                    session.Changed += (_, _) => OnPropertyChanged(nameof(WindowTitle));
                }

                OnPropertyChanged(nameof(WindowTitle));
            }
        };
        ready = true;
    }

    public AppServices Services { get; }

    public IFileDialogs Files { get; }

    public ProjectWorkspace Workspace { get; }

    public ProjectViewModel Project { get; }

    public MachiningViewModel Machining { get; }

    public LibraryViewModel Library { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public string WindowTitle => Workspace.Session is { } session ? $"{session.Project.Name} - 3C Tool" : "3C Tool";

    [ObservableProperty]
    public partial PageViewModel SelectedPage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; private set; }

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlay))]
    public partial ViewModelBase? Overlay { get; private set; }

    public bool HasOverlay => Overlay is not null;

    /// <summary>Loads the settings and shows the first page.</summary>
    public async Task InitializeAsync()
    {
        if (await RunAsync(() => Services.LoadAsync()))
        {
            await SelectedPage.ShowAsync();
        }
    }

    public void ShowMessage(string message, bool isError = false)
    {
        Message = message;
        MessageIsError = isError;
    }

    public void Dispose() => Machining.Dispose();

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false)
    {
        // One question at a time: wait for an open one to be answered first.
        while (Overlay is ConfirmViewModel open)
        {
            await open.Result;
            await Task.Yield();
        }

        var confirm = new ConfirmViewModel(title, message, confirmText, danger);
        Overlay = confirm;
        try
        {
            return await confirm.Result;
        }
        finally
        {
            if (ReferenceEquals(Overlay, confirm))
            {
                Overlay = null;
            }
        }
    }

    public async Task<bool> RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        IsBusy = ++busy > 0;
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowMessage(Describe(ex), isError: true);
            return false;
        }
        finally
        {
            IsBusy = --busy > 0;
        }
    }

    private static bool IsExpected(Exception ex) => ex
        is LibraryValidationException
        or Post.PostException
        or IOException
        or KeyNotFoundException
        or UnauthorizedAccessException
        or InvalidOperationException
        or InvalidDataException
        or FormatException
        or NotSupportedException
        or ArgumentException
        or DbException
        or DbUpdateException
        or ILGPU.Runtime.AcceleratorException
        or ILGPU.InternalCompilerException
        or ILGPU.Backends.InvalidCodeGenerationException;

    private static string Describe(Exception ex) => ex switch
    {
        DbUpdateException { InnerException: { } inner } => "Could not save to the library: " + inner.Message,
        ILGPU.Runtime.AcceleratorException or ILGPU.InternalCompilerException or ILGPU.Backends.InvalidCodeGenerationException =>
            $"The compute device failed ({ex.GetType().Name}). Select the CPU backend in Settings and try again.",
        _ => ex.Message,
    };

    partial void OnSelectedPageChanged(PageViewModel value)
    {
        if (ready)
        {
            _ = value.ShowAsync();
        }
    }

    [RelayCommand]
    private void DismissMessage()
    {
        Message = null;
        MessageIsError = false;
    }
}
