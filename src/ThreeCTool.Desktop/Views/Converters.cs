using Avalonia.Data.Converters;

using ThreeCTool.Core.Materials;
using ThreeCTool.Desktop.ViewModels;

namespace ThreeCTool.Desktop.Views;

public static class Converters
{
    /// <summary>", built-in" for built-in library entries.</summary>
    public static FuncValueConverter<bool, string> BuiltInSuffix { get; } = new(builtIn => builtIn ? ", built-in" : string.Empty);

    /// <summary>Size and material of a stock size list entry: values are the entry and the stock sizes page.</summary>
    public static FuncMultiValueConverter<object?, string> StockSizeText { get; } = new(values =>
    {
        var list = values.ToList();
        return list is [StockSize size, StockSizesViewModel page] ? page.Describe(size) : string.Empty;
    });
}
