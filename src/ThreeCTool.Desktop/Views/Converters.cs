using Avalonia.Data.Converters;

namespace ThreeCTool.Desktop.Views;

public static class Converters
{
    /// <summary>", built-in" for built-in library entries.</summary>
    public static FuncValueConverter<bool, string> BuiltInSuffix { get; } = new(builtIn => builtIn ? ", built-in" : string.Empty);
}
