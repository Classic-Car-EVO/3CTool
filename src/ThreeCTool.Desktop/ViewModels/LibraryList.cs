using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Desktop.Services;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>An entry box whose text is parsed into a model; <see cref="Error"/> is set while the text is invalid.</summary>
public interface IEditField
{
    string? EntryError { get; }
}

/// <summary>
/// A list of number pairs entered as text, such as a chip load table <c>3=0.02; 6=0.04</c> or holder steps
/// <c>20x15; 32x10</c>. Valid text is written to the model immediately.
/// </summary>
public sealed partial class TableField : ObservableObject, IEditField
{
    private readonly Func<IEnumerable<(double A, double B)>> get;
    private readonly Action<List<(double A, double B)>> set;
    private readonly char separator;
    private bool refreshing;

    public TableField(string label, string hint, char separator, Func<IEnumerable<(double A, double B)>> get, Action<List<(double A, double B)>> set)
    {
        Label = label;
        Hint = hint;
        this.separator = separator;
        this.get = get;
        this.set = set;
        Refresh();
    }

    public string Label { get; }

    /// <summary>Format help shown as the watermark, e.g. <c>diameter=chip load; ...</c>.</summary>
    public string Hint { get; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    string? IEditField.EntryError => Error;

    public static bool TryParse(string text, char separator, out List<(double A, double B)> pairs)
    {
        pairs = [];
        foreach (var entry in text.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(separator, StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                || !double.IsFinite(a) || !double.IsFinite(b))
            {
                return false;
            }

            pairs.Add((a, b));
        }

        return true;
    }

    public void Refresh()
    {
        refreshing = true;
        try
        {
            Text = string.Join("; ", get().Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.A:0.######}{separator}{p.B:0.######}")));
            Error = null;
        }
        finally
        {
            refreshing = false;
        }
    }

    partial void OnTextChanged(string value)
    {
        if (refreshing)
        {
            return;
        }

        if (TryParse(value, separator, out var pairs))
        {
            Error = null;
            set(pairs);
        }
        else
        {
            Error = $"{Label}: use the form {Hint}.";
        }
    }
}

/// <summary>Edits a detached copy of a library entry; nothing is stored until the page saves it.</summary>
public abstract class EditorViewModel<T> : ViewModelBase
    where T : class
{
    private readonly List<IEditField> fields = [];
    private byte[] original;

    protected EditorViewModel(T model, bool isNew, LengthUnit unit)
    {
        Model = model;
        IsNew = isNew;
        Unit = unit;
        original = EntityCopy.Canonical(model);
    }

    public T Model { get; }

    /// <summary>Not stored yet (new or duplicated entry).</summary>
    public bool IsNew { get; }

    public LengthUnit Unit { get; }

    public virtual bool IsReadOnly => false;

    public bool IsEditable => !IsReadOnly;

    public bool HasChanges => IsNew || !EntityCopy.Canonical(Model).AsSpan().SequenceEqual(original);

    /// <summary>First entry error, or null when every field holds a valid value.</summary>
    public string? FirstError => AllFields().Select(f => f.EntryError).FirstOrDefault(e => e is not null);

    /// <summary>The current model state counts as unchanged (after it was stored).</summary>
    public void MarkSaved() => original = EntityCopy.Canonical(Model);

    protected NumberField Length(string label, Func<double> get, Action<double> set) => Track(NumberField.Length(label, get, set, Unit));

    protected NumberField Number(string label, Func<double> get, Action<double> set) => Track(NumberField.Number(label, get, set));

    protected TField Track<TField>(TField field)
        where TField : IEditField
    {
        fields.Add(field);
        return field;
    }

    /// <summary>All entry fields including those of table rows.</summary>
    protected virtual IEnumerable<IEditField> AllFields() => fields;
}

/// <summary>
/// A library list with an editor for the selected entry: new, duplicate, save, revert and delete. Switching away from
/// an entry with unsaved changes asks first.
/// </summary>
public abstract partial class LibraryListViewModel<TItem, TEditor>(IShell shell, string title) : PageViewModel(shell, title)
    where TItem : class
    where TEditor : EditorViewModel<TItem>
{
    private bool suppressSelection;

    public ObservableCollection<TItem> Items { get; } = [];

    [ObservableProperty]
    public partial TItem? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand), nameof(DuplicateCommand), nameof(DeleteCommand))]
    public partial TEditor? Editor { get; set; }

    protected LengthUnit Unit => Shell.Services.Settings.DisplayUnit;

    public override Task ShowAsync() =>
        Editor is { HasChanges: true } ? Task.CompletedTask : ReloadAsync(Editor is { } e ? IdOf(e.Model) : null);

    /// <summary>Reloads the list and selects <paramref name="select"/> (or the first entry).</summary>
    public async Task ReloadAsync(Guid? select = null)
    {
        await Shell.RunAsync(async () =>
        {
            var items = await ListAsync();
            await OnListedAsync();
            suppressSelection = true;
            try
            {
                Items.Clear();
                foreach (var item in items)
                {
                    Items.Add(item);
                }

                Selected = Items.FirstOrDefault(i => IdOf(i) == select) ?? Items.FirstOrDefault();
            }
            finally
            {
                suppressSelection = false;
            }

            Editor = Selected is null ? null : CreateEditor(EntityCopy.Clone(Selected), false);
            OnEditorReplaced();
        });
    }

    protected abstract Task<IReadOnlyList<TItem>> ListAsync();

    /// <summary>Loads data shown alongside the list (usage, names); runs inside the reload.</summary>
    protected virtual Task OnListedAsync() => Task.CompletedTask;

    protected abstract Task SaveItemAsync(TItem item);

    protected abstract Task DeleteItemAsync(TItem item);

    protected abstract Guid IdOf(TItem item);

    protected abstract string NameOf(TItem item);

    protected abstract TItem CreateNew();

    protected abstract TItem DuplicateItem(TItem item);

    protected abstract TEditor CreateEditor(TItem item, bool isNew);

    /// <summary>Why <paramref name="item"/> cannot be deleted, or null.</summary>
    protected virtual string? DeleteBlocker(TItem item) => null;

    protected virtual void OnEditorReplaced()
    {
    }

    partial void OnSelectedChanged(TItem? oldValue, TItem? newValue)
    {
        if (suppressSelection)
        {
            return;
        }

        if (Editor is { HasChanges: true })
        {
            _ = ConfirmSwitchAsync(oldValue, newValue);
            return;
        }

        ShowEditorFor(newValue);
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (await ConfirmDiscardAsync())
        {
            SetSelected(null);
            Editor = CreateEditor(CreateNew(), true);
            OnEditorReplaced();
        }
    }

    private bool HasEditor() => Editor is not null;

    private bool HasStoredEditor() => Editor is { IsNew: false };

    private bool HasWritableEditor() => Editor is { IsReadOnly: false };

    [RelayCommand(CanExecute = nameof(HasStoredEditor))]
    private async Task DuplicateAsync()
    {
        if (Editor is { } editor && await ConfirmDiscardAsync())
        {
            var source = Items.FirstOrDefault(i => IdOf(i) == IdOf(editor.Model)) ?? editor.Model;
            SetSelected(null);
            Editor = CreateEditor(DuplicateItem(EntityCopy.Clone(source)), true);
            OnEditorReplaced();
        }
    }

    [RelayCommand(CanExecute = nameof(HasWritableEditor))]
    private async Task SaveAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (editor.FirstError is { } error)
        {
            Shell.ShowMessage(error, isError: true);
            return;
        }

        if (await Shell.RunAsync(() => SaveItemAsync(editor.Model)))
        {
            editor.MarkSaved();
            await ReloadAsync(IdOf(editor.Model));
            Shell.ShowMessage($"Saved '{NameOf(editor.Model)}'.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private void Revert() => ShowEditorFor(Selected);

    [RelayCommand(CanExecute = nameof(HasStoredEditor))]
    private async Task DeleteAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (DeleteBlocker(editor.Model) is { } blocker)
        {
            Shell.ShowMessage(blocker, isError: true);
            return;
        }

        var name = NameOf(editor.Model);
        if (await Shell.ConfirmAsync("Delete", $"Delete '{name}' from the library?", "Delete", danger: true)
            && await Shell.RunAsync(() => DeleteItemAsync(editor.Model)))
        {
            Editor = null;
            await ReloadAsync();
            Shell.ShowMessage($"Deleted '{name}'.");
        }
    }

    private void ShowEditorFor(TItem? item)
    {
        Editor = item is null ? null : CreateEditor(EntityCopy.Clone(item), false);
        OnEditorReplaced();
    }

    private async Task<bool> ConfirmDiscardAsync() =>
        Editor is not { HasChanges: true } editor
        || await Shell.ConfirmAsync("Unsaved changes", $"Discard the unsaved changes to '{NameOf(editor.Model)}'?", "Discard", danger: true);

    private async Task ConfirmSwitchAsync(TItem? previous, TItem? next)
    {
        // Keep the edited entry selected while asking; the list control is still processing the click.
        await Task.Yield();
        SetSelected(previous);
        if (await ConfirmDiscardAsync())
        {
            SetSelected(next);
            ShowEditorFor(next);
        }
    }

    private void SetSelected(TItem? item)
    {
        suppressSelection = true;
        try
        {
            Selected = item;
        }
        finally
        {
            suppressSelection = false;
        }
    }
}
