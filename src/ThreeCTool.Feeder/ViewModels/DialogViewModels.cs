using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ThreeCTool.Feeder.ViewModels;

/// <summary>On-screen keyboard (the Pi kiosk has no physical keyboard or system input method).</summary>
public sealed partial class KeyboardViewModel : ViewModelBase
{
    public const int MaxLength = 200;

    private static readonly string[] LetterRows = ["1234567890", "qwertyuiop", "asdfghjkl:", "zxcvbnm-_.", "/@#()+,"];
    private static readonly string[] NumberRows = ["789", "456", "123", "-0."];

    private readonly TaskCompletionSource<string?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public KeyboardViewModel(string title, string initial, bool numeric)
    {
        Title = title;
        Numeric = numeric;
        Text = initial ?? string.Empty;
        Rows = BuildRows();
    }

    public string Title { get; }

    public bool Numeric { get; }

    public bool Letters => !Numeric;

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool Shift { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<IReadOnlyList<string>> Rows { get; private set; }

    public Task<string?> Result => result.Task;

    [RelayCommand]
    public void Press(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Text.Length + key.Length <= MaxLength)
        {
            Text += key;
        }

        if (Shift)
        {
            Shift = false;
        }
    }

    [RelayCommand]
    public void Backspace()
    {
        if (Text.Length > 0)
        {
            Text = Text[..^1];
        }
    }

    [RelayCommand]
    public void Space() => Press(" ");

    [RelayCommand]
    public void Clear() => Text = string.Empty;

    [RelayCommand]
    public void ToggleShift() => Shift = !Shift;

    [RelayCommand]
    public void Accept() => result.TrySetResult(Text);

    [RelayCommand]
    public void Cancel() => result.TrySetResult(null);

    partial void OnShiftChanged(bool value) => Rows = BuildRows();

    private string[][] BuildRows() =>
        [.. (Numeric ? NumberRows : LetterRows).Select(row => row.Select(c => (Shift ? char.ToUpperInvariant(c) : c).ToString()).ToArray())];
}

/// <summary>Yes/no question shown over everything else.</summary>
public sealed partial class ConfirmViewModel(string title, string message, string confirmText, bool danger) : ViewModelBase
{
    private readonly TaskCompletionSource<bool> result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Title { get; } = title;

    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    public bool Danger { get; } = danger;

    public Task<bool> Result => result.Task;

    [RelayCommand]
    public void Accept() => result.TrySetResult(true);

    [RelayCommand]
    public void Cancel() => result.TrySetResult(false);
}

/// <summary>A row of mutually exclusive touch buttons (ratings, jog step, jog feed).</summary>
public sealed partial class ChoiceGroup : ViewModelBase
{
    public ChoiceGroup(string label, IEnumerable<(double Value, string Label)> items, double value)
    {
        Label = label;
        Items = [.. items.Select(i => new ChoiceItem(this, i.Value, i.Label))];
        Value = value;
        Select(value);
    }

    public string Label { get; }

    public IReadOnlyList<ChoiceItem> Items { get; }

    [ObservableProperty]
    public partial double Value { get; set; }

    partial void OnValueChanged(double value) => Select(value);

    private void Select(double value)
    {
        foreach (var item in Items)
        {
            item.IsSelected = item.Value.Equals(value);
        }
    }
}

public sealed partial class ChoiceItem(ChoiceGroup group, double value, string label) : ViewModelBase
{
    public double Value { get; } = value;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    public void Select() => group.Value = Value;
}
