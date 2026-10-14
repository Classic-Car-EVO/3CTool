using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Desktop.Services;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>A file type filter for the file dialogs (patterns such as <c>*.stl</c>).</summary>
public sealed record FileType(string Name, IReadOnlyList<string> Patterns)
{
    public static FileType Any { get; } = new("All files", ["*"]);
}

/// <summary>Native file dialogs; null when cancelled. Replaced by a fake in tests.</summary>
public interface IFileDialogs
{
    Task<string?> OpenFileAsync(string title, IReadOnlyList<FileType> types);

    Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileType> types);

    Task<string?> PickFolderAsync(string title);
}

/// <summary>What pages need from the main window: services, dialogs, messages and error handling.</summary>
public interface IShell
{
    AppServices Services { get; }

    IFileDialogs Files { get; }

    void ShowMessage(string message, bool isError = false);

    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false);

    /// <summary>Runs <paramref name="action"/> while busy; expected failures are shown to the user and false returned.</summary>
    Task<bool> RunAsync(Func<Task> action);
}

/// <summary>A top-level page of the main window.</summary>
public abstract class PageViewModel(IShell shell, string title) : ViewModelBase
{
    public IShell Shell { get; } = shell;

    public string Title { get; } = title;

    /// <summary>Called whenever the page is selected; reloads its data.</summary>
    public virtual Task ShowAsync() => Task.CompletedTask;
}

/// <summary>Modal confirmation shown over the window.</summary>
public sealed partial class ConfirmViewModel(string title, string message, string confirmText, bool danger) : ViewModelBase
{
    private readonly TaskCompletionSource<bool> result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Title { get; } = title;

    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    public bool IsDanger { get; } = danger;

    public Task<bool> Result => result.Task;

    [RelayCommand]
    private void Confirm() => result.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => result.TrySetResult(false);
}
