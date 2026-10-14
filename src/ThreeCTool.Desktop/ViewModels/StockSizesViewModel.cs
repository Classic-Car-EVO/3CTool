using CommunityToolkit.Mvvm.ComponentModel;

using ThreeCTool.Core;
using ThreeCTool.Core.Materials;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>Edits a standard material size: length × width × thickness, optionally for one material.</summary>
public sealed partial class StockSizeEditorViewModel : EditorViewModel<StockSize>
{
    public StockSizeEditorViewModel(StockSize size, bool isNew, LengthUnit unit, IReadOnlyList<Choice<Guid?>> materials)
        : base(size, isNew, unit)
    {
        Materials = materials;
        SizeX = Track(NumberField.PositiveLength("Length (X)", () => size.SizeX, v => size.SizeX = v, unit));
        SizeY = Track(NumberField.PositiveLength("Width (Y)", () => size.SizeY, v => size.SizeY = v, unit));
        SizeZ = Track(NumberField.PositiveLength("Thickness (Z)", () => size.SizeZ, v => size.SizeZ = v, unit));
        Material = materials.FirstOrDefault(m => m.Value == size.MaterialId) ?? materials[0];
    }

    public IReadOnlyList<Choice<Guid?>> Materials { get; }

    [ObservableProperty]
    public partial Choice<Guid?> Material { get; set; }

    public NumberField SizeX { get; }

    public NumberField SizeY { get; }

    public NumberField SizeZ { get; }

    partial void OnMaterialChanged(Choice<Guid?> value)
    {
        if (value is not null)
        {
            Model.MaterialId = value.Value;
        }
    }
}

/// <summary>Standard sizes of box material that the stock of a project can be made of.</summary>
public sealed class StockSizesViewModel(IShell shell) : LibraryListViewModel<StockSize, StockSizeEditorViewModel>(shell, "Stock sizes")
{
    public const string AnyMaterial = "Any material";

    private IReadOnlyList<Choice<Guid?>> materials = [new(AnyMaterial, null)];

    /// <summary>Material name of a size for the list.</summary>
    public string MaterialName(StockSize size)
    {
        ArgumentNullException.ThrowIfNull(size);
        return materials.FirstOrDefault(m => m.Value == size.MaterialId)?.Label ?? AnyMaterial;
    }

    /// <summary>Size text such as "2440 × 1220 × 18 mm" in the display unit.</summary>
    public static string SizeText(StockSize size, LengthUnit unit)
    {
        ArgumentNullException.ThrowIfNull(size);
        return $"{NumberField.FormatLength(size.SizeX, unit)} × {NumberField.FormatLength(size.SizeY, unit)} × {NumberField.FormatLength(size.SizeZ, unit)} {NumberField.Abbreviation(unit)}";
    }

    /// <summary>Second line of a list entry: size and material.</summary>
    public string Describe(StockSize size) => $"{SizeText(size, Unit)}, {MaterialName(size)}";

    protected override Task<IReadOnlyList<StockSize>> ListAsync() => Shell.Services.Library.ListStockSizesAsync();

    protected override async Task OnListedAsync()
    {
        var list = await Shell.Services.Library.ListMaterialsAsync();
        materials = [new(AnyMaterial, null), .. list.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Select(m => new Choice<Guid?>(m.Name, m.Id))];
    }

    protected override Task SaveItemAsync(StockSize item) => Shell.Services.Library.SaveStockSizeAsync(item);

    protected override Task DeleteItemAsync(StockSize item) => Shell.Services.Library.DeleteStockSizeAsync(item.Id);

    protected override Guid IdOf(StockSize item) => item.Id;

    protected override string NameOf(StockSize item) => item.Name;

    protected override StockSize CreateNew() => new() { Name = "New stock size", SizeX = 600, SizeY = 300, SizeZ = 18 };

    protected override StockSize DuplicateItem(StockSize item)
    {
        item.Id = Guid.CreateVersion7();
        item.Name += " (copy)";
        return item;
    }

    protected override StockSizeEditorViewModel CreateEditor(StockSize item, bool isNew) => new(item, isNew, Unit, materials);
}
