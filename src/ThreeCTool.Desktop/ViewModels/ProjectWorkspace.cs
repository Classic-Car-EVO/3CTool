using CommunityToolkit.Mvvm.ComponentModel;

using ThreeCTool.Desktop.Services;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>
/// The open project, shared by the project and CAM pages. <see cref="ProjectSession"/> uses one database context, so
/// every change goes through <see cref="EditAsync"/>, which runs them one at a time.
/// </summary>
public sealed partial class ProjectWorkspace(IShell shell) : ObservableObject
{
    private Task tail = Task.CompletedTask;

    /// <summary>Raised after every successful edit made through <see cref="EditAsync"/>.</summary>
    public event EventHandler? Edited;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    public partial ProjectSession? Session { get; private set; }

    public bool HasProject => Session is not null;

    /// <summary>
    /// Runs <paramref name="action"/> on the open project after any edit still in progress; failures are shown to the
    /// user. Returns false when there is no project, the action failed or was cancelled.
    /// </summary>
    public async Task<bool> EditAsync(Func<ProjectSession, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var ok = await QueueAsync(() => Session is { } session ? shell.RunAsync(() => action(session)) : Task.FromResult(false));
        if (ok)
        {
            Edited?.Invoke(this, EventArgs.Empty);
        }

        return ok;
    }

    /// <summary>Closes the open project (if any) and switches to <paramref name="session"/>.</summary>
    public Task ReplaceAsync(ProjectSession? session) => QueueAsync(async () =>
    {
        var old = Session;
        Session = session;
        if (old is not null)
        {
            await old.DisposeAsync();
        }

        return true;
    });

    private async Task<bool> QueueAsync(Func<Task<bool>> action)
    {
        var previous = tail;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tail = done.Task;
        try
        {
            await previous;
            return await action();
        }
        finally
        {
            done.SetResult();
        }
    }
}
