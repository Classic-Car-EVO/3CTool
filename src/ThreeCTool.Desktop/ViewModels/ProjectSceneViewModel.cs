using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ThreeCTool.Desktop.Services;
using ThreeCTool.Geometry;
using ThreeCTool.Rendering;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Desktop.ViewModels;

/// <summary>3D view of the project: part bodies, stock and keep-out zones in the part frame.</summary>
public sealed partial class ProjectSceneViewModel : ObservableObject
{
    public CamScene Scene { get; } = new();

    /// <summary>Bounds of each model in the part frame (after its transforms), from the last refresh.</summary>
    public IReadOnlyDictionary<Guid, BoundingBox> PartBounds { get; private set; } = new Dictionary<Guid, BoundingBox>();

    [ObservableProperty]
    public partial bool ShowPart { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowStock { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowKeepOuts { get; set; } = true;

    /// <summary>Rebuilds the meshes from the project (model files are loaded once per session).</summary>
    public async Task RefreshAsync(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var parts = new List<LoadedPart>();
        foreach (var model in session.Parts)
        {
            var part = await session.LoadPartAsync(model);
            parts.Add(part with { Model = EntityCopy.Clone(part.Model) });
        }

        var folder = session.Folder;
        var stock = EntityCopy.Clone(session.Stock);
        var zones = session.KeepOuts.Select(EntityCopy.Clone).ToList();
        var setups = session.Setups.Select(EntityCopy.Clone).ToList();
        var (meshes, bounds) = await Task.Run(() =>
        {
            var solid = PartGeometry.StockMesh(stock, PartGeometry.LoadStockMesh(folder, stock));
            var frames = solid is null || solid.TriangleCount == 0
                ? []
                : setups.ToDictionary(s => s.Id, s => MachiningSetup.CreateFrame(s, stock, solid.Bounds));
            var meshes = SceneBuilder.ProjectMeshes(parts, solid, zones, id => frames.GetValueOrDefault(id));
            var bounds = parts.ToDictionary(p => p.Model.Id, p => p.AllBodies().Bounds);
            return (meshes, bounds);
        });

        var first = Scene.Meshes.Count == 0;
        Scene.SetMeshes(meshes);
        PartBounds = bounds;
        if (first && meshes.Count > 0)
        {
            Scene.Fit();
        }
    }

    public void Clear()
    {
        Scene.SetMeshes([]);
        PartBounds = new Dictionary<Guid, BoundingBox>();
    }

    [RelayCommand]
    private void SetView(CameraView view) => Scene.SetView(view);

    [RelayCommand]
    private void Fit() => Scene.Fit();

    partial void OnShowPartChanged(bool value) => Scene.SetVisible(SceneLayer.Part, value);

    partial void OnShowStockChanged(bool value) => Scene.SetVisible(SceneLayer.Stock, value);

    partial void OnShowKeepOutsChanged(bool value) => Scene.SetVisible(SceneLayer.KeepOut, value);
}
