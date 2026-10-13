using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using ThreeCTool.Core;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>
/// Text entry for a number stored on a model. Lengths are shown in the display unit and accept any unit suffix
/// (<c>6</c>, <c>6 mm</c>, <c>1/4"</c>); the model is updated on every valid edit, invalid text sets <see cref="Error"/>.
/// </summary>
public sealed partial class NumberField : ObservableObject, IEditField
{
    private readonly Func<double?> get;
    private readonly Action<double?> set;
    private readonly LengthUnit? unit;
    private readonly bool optional;
    private readonly bool integer;
    private readonly bool positive;
    private bool refreshing;

    private NumberField(string label, Func<double?> get, Action<double?> set, LengthUnit? unit, bool optional, bool integer, bool positive = false)
    {
        this.get = get;
        this.set = set;
        this.unit = unit;
        this.optional = optional;
        this.integer = integer;
        this.positive = positive;
        Label = label;
        Refresh();
    }

    /// <summary>Raised after a valid edit changed the model.</summary>
    public event EventHandler? Changed;

    public string Label { get; }

    /// <summary>Unit abbreviation shown next to the box (empty for plain numbers).</summary>
    public string UnitText => unit is { } u ? Abbreviation(u) : string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    string? IEditField.EntryError => Error;

    public static NumberField Length(string label, Func<double> get, Action<double> set, LengthUnit unit) =>
        new(label, () => get(), v => set(v!.Value), unit, optional: false, integer: false);

    /// <summary>A length that must be greater than zero (sizes, diameters).</summary>
    public static NumberField PositiveLength(string label, Func<double> get, Action<double> set, LengthUnit unit) =>
        new(label, () => get(), v => set(v!.Value), unit, optional: false, integer: false, positive: true);

    public static NumberField OptionalLength(string label, Func<double?> get, Action<double?> set, LengthUnit unit) =>
        new(label, get, set, unit, optional: true, integer: false);

    public static NumberField Number(string label, Func<double> get, Action<double> set) =>
        new(label, () => get(), v => set(v!.Value), null, optional: false, integer: false);

    public static NumberField WholeNumber(string label, Func<int> get, Action<int> set) =>
        new(label, () => get(), v => set((int)v!.Value), null, optional: false, integer: true);

    public static string Abbreviation(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeter => "mm",
        LengthUnit.Centimeter => "cm",
        LengthUnit.Meter => "m",
        _ => "in",
    };

    /// <summary>Formats millimetres in <paramref name="unit"/> without the unit suffix.</summary>
    public static string FormatLength(double millimeters, LengthUnit unit) =>
        Units.FromMillimeters(millimeters, unit).ToString(unit == LengthUnit.Inch ? "0.#####" : "0.####", CultureInfo.InvariantCulture);

    /// <summary>Re-reads the model (after it was changed elsewhere).</summary>
    public void Refresh()
    {
        refreshing = true;
        try
        {
            Text = get() is { } value
                ? unit is { } u ? FormatLength(value, u) : value.ToString(integer ? "0" : "0.######", CultureInfo.InvariantCulture)
                : string.Empty;
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

        if (!TryParse(value, out var parsed, out var error))
        {
            Error = error;
            return;
        }

        Error = null;
        if (get() != parsed)
        {
            set(parsed);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryParse(string text, out double? value, out string? error)
    {
        value = null;
        error = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            error = optional ? null : $"{Label} is required.";
            return optional;
        }

        var negative = trimmed.StartsWith('-');
        var body = negative ? trimmed[1..].TrimStart() : trimmed;
        double magnitude;
        if (unit is { } u)
        {
            if (!Units.TryParseLength(body, u, out magnitude))
            {
                error = $"{Label}: enter a length such as 6, 6 mm or 1/4\".";
                return false;
            }
        }
        else if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out magnitude) || !double.IsFinite(magnitude))
        {
            error = $"{Label}: enter a number.";
            return false;
        }

        if (integer && (magnitude != Math.Floor(magnitude) || magnitude > int.MaxValue))
        {
            error = $"{Label}: enter a whole number.";
            return false;
        }

        value = negative ? -magnitude : magnitude;
        if (positive && value <= 0)
        {
            error = $"{Label} must be greater than zero.";
            value = null;
            return false;
        }

        return true;
    }
}
