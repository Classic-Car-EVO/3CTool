using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Core;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Tooling;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>Cutting data of a material for one tool material.</summary>
public sealed class CuttingDataRow : ViewModelBase
{
    public CuttingDataRow(CuttingData data)
    {
        Data = data;
        SpeedMin = NumberField.Number("Surface speed min", () => data.SurfaceSpeedMin, v => data.SurfaceSpeedMin = v);
        SpeedMax = NumberField.Number("Surface speed max", () => data.SurfaceSpeedMax, v => data.SurfaceSpeedMax = v);
        ChipLoad = new TableField(
            "Chip load (mm/tooth)",
            "tool diameter=chip load; ...",
            '=',
            () => data.ChipLoad.Select(p => (p.Diameter, p.ChipLoad)),
            pairs => data.ChipLoad = [.. pairs.Select(p => new ChipLoadPoint { Diameter = p.A, ChipLoad = p.B })]);
    }

    public CuttingData Data { get; }

    public NumberField SpeedMin { get; }

    public NumberField SpeedMax { get; }

    public TableField ChipLoad { get; }

    public IEnumerable<IEditField> Fields => [SpeedMin, SpeedMax, ChipLoad];
}

public sealed partial class MaterialEditorViewModel : EditorViewModel<Material>
{
    public MaterialEditorViewModel(Material material, bool isNew, LengthUnit unit)
        : base(material, isNew, unit)
    {
        CuttingForce = Number("kc1.1 (N/mm²)", () => material.SpecificCuttingForce, v => material.SpecificCuttingForce = v);
        KienzleExponent = Number("Kienzle exponent mc", () => material.KienzleExponent, v => material.KienzleExponent = v);
        ToleranceScale = Number("Tolerance scale", () => material.ToleranceScale, v => material.ToleranceScale = v);
        MaxDocFactor = Number("Max depth of cut (× D)", () => material.MaxDocFactor, v => material.MaxDocFactor = v);
        MaxWocFactor = Number("Max width of cut (× D)", () => material.MaxWocFactor, v => material.MaxWocFactor = v);
        CuttingData = [.. material.CuttingData.Select(d => new CuttingDataRow(d))];
    }

    public static IReadOnlyList<MaterialCategory> Categories { get; } = Enum.GetValues<MaterialCategory>();

    public static IReadOnlyList<ToolMaterial> ToolMaterials { get; } = Enum.GetValues<ToolMaterial>();

    /// <summary>Built-in materials are updated with the application; duplicate them to change values.</summary>
    public override bool IsReadOnly => Model.IsBuiltIn && !IsNew;

    public NumberField CuttingForce { get; }

    public NumberField KienzleExponent { get; }

    public NumberField ToleranceScale { get; }

    public NumberField MaxDocFactor { get; }

    public NumberField MaxWocFactor { get; }

    public ObservableCollection<CuttingDataRow> CuttingData { get; }

    protected override IEnumerable<IEditField> AllFields() => base.AllFields().Concat(CuttingData.SelectMany(r => r.Fields));

    [RelayCommand]
    private void AddCuttingData()
    {
        var used = Model.CuttingData.Select(d => d.ToolMaterial).ToHashSet();
        var data = new CuttingData
        {
            ToolMaterial = ToolMaterials.FirstOrDefault(m => !used.Contains(m)),
            SurfaceSpeedMin = 100,
            SurfaceSpeedMax = 300,
            ChipLoad = [new() { Diameter = 3, ChipLoad = 0.02 }, new() { Diameter = 6, ChipLoad = 0.04 }],
        };
        Model.CuttingData.Add(data);
        CuttingData.Add(new CuttingDataRow(data));
    }

    [RelayCommand]
    private void RemoveCuttingData(CuttingDataRow row)
    {
        Model.CuttingData.Remove(row.Data);
        CuttingData.Remove(row);
    }
}

public sealed class MaterialsViewModel(IShell shell) : LibraryListViewModel<Material, MaterialEditorViewModel>(shell, "Materials")
{
    protected override Task<IReadOnlyList<Material>> ListAsync() => Shell.Services.Library.ListMaterialsAsync();

    protected override Task SaveItemAsync(Material item) => Shell.Services.Library.SaveMaterialAsync(item);

    protected override Task DeleteItemAsync(Material item) => Shell.Services.Library.DeleteMaterialAsync(item.Id);

    protected override Guid IdOf(Material item) => item.Id;

    protected override string NameOf(Material item) => item.Name;

    protected override Material CreateNew() => new()
    {
        Name = "New material",
        Category = MaterialCategory.Wood,
        SpecificCuttingForce = 40,
        CuttingData =
        [
            new()
            {
                ToolMaterial = ToolMaterial.Carbide,
                SurfaceSpeedMin = 200,
                SurfaceSpeedMax = 600,
                ChipLoad = [new() { Diameter = 3, ChipLoad = 0.03 }, new() { Diameter = 6, ChipLoad = 0.06 }],
            },
        ],
    };

    protected override Material DuplicateItem(Material item)
    {
        item.Id = Guid.CreateVersion7();
        item.Name += " (copy)";
        item.IsBuiltIn = false;
        return item;
    }

    protected override MaterialEditorViewModel CreateEditor(Material item, bool isNew) => new(item, isNew, Unit);

    protected override string? DeleteBlocker(Material item) =>
        item.IsBuiltIn ? "Built-in materials cannot be deleted; duplicate and edit them instead." : null;
}
