using Microsoft.EntityFrameworkCore;

using ThreeCTool.Core;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Data;
using ThreeCTool.Geometry;

namespace ThreeCTool.Desktop.Services;

/// <summary>Result of importing a model file into the project.</summary>
public sealed record PartImport(PartModel Model, LoadedPart Loaded, bool UnitsDeclared);

/// <summary>
/// An open project folder: <c>project.db</c> plus the copied model files and caches. Entities are tracked by one
/// long-lived context, so every member must be called from one logical thread (the UI thread); background work gets
/// copies (see <see cref="EntityCopy"/>). Library entities used by the project are snapshots in the project database.
/// </summary>
public sealed class ProjectSession : IAsyncDisposable
{
    public const string ModelsFolder = "models";
    public const string ToolpathsFolder = "toolpaths";

    private readonly ProjectDbContext db;
    private readonly TimeProvider time;
    private readonly Dictionary<Guid, LoadedPart> loaded = [];
    private readonly List<PartModel> parts;
    private readonly List<KeepOutZone> keepOuts;
    private readonly List<Setup> setups;
    private readonly List<ProjectTool> projectTools;

    private ProjectSession(string folder, string libraryPath, ProjectDbContext db, TimeProvider time, Project project, Stock stock,
        List<PartModel> parts, List<KeepOutZone> keepOuts, List<Setup> setups, List<ProjectTool> projectTools)
    {
        Folder = folder;
        LibraryPath = libraryPath;
        this.db = db;
        this.time = time;
        Project = project;
        Stock = stock;
        this.parts = parts;
        this.keepOuts = keepOuts;
        this.setups = setups;
        this.projectTools = projectTools;
        Machine = new MachineProfile();
        Tools = [];
    }

    /// <summary>Raised after every saved change (the pipeline uses it to drop stale results).</summary>
    public event EventHandler? Changed;

    public string Folder { get; }

    public string LibraryPath { get; }

    public Project Project { get; }

    public Stock Stock { get; }

    public IReadOnlyList<PartModel> Parts => parts;

    public IReadOnlyList<KeepOutZone> KeepOuts => keepOuts;

    /// <summary>Setups in machining order, each with its operations sorted by <see cref="Operation.Order"/>.</summary>
    public IReadOnlyList<Setup> Setups => setups;

    /// <summary>Machine snapshot (detached).</summary>
    public MachineProfile Machine { get; private set; }

    /// <summary>Material snapshot (detached), null until chosen.</summary>
    public Material? Material { get; private set; }

    /// <summary>Snapshots of the project's tools (detached).</summary>
    public IReadOnlyList<Tool> Tools { get; private set; }

    internal ProjectDbContext Database => db;

    public static bool IsProjectFolder(string folder) => File.Exists(Path.Combine(folder, ProjectDatabase.FileName));

    /// <summary>Creates a project in an empty (or new) folder with snapshots of the chosen machine and material.</summary>
    public static async Task<ProjectSession> CreateAsync(
        string folder,
        string libraryPath,
        string name,
        Guid machineId,
        Guid? materialId,
        OptimizationGoal goal = OptimizationGoal.Quality,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        folder = Path.GetFullPath(folder);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new IOException($"'{folder}' is not empty; choose an empty folder for a new project.");
        }

        Directory.CreateDirectory(folder);
        time ??= TimeProvider.System;
        var now = time.GetUtcNow();
        await using (var library = await LibraryDatabase.OpenAsync(libraryPath, cancellationToken).ConfigureAwait(false))
        await using (var snapshot = await ProjectDatabase.OpenAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            var machine = await library.MachineProfiles.AsNoTracking().SingleOrDefaultAsync(m => m.Id == machineId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The selected machine is not in the library.");
            var material = materialId is { } mid
                ? await library.Materials.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mid, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The selected material is not in the library.")
                : null;
            await ProjectSnapshot.CaptureMachineAsync(library, snapshot, machineId, cancellationToken).ConfigureAwait(false);
            if (material is not null)
            {
                await ProjectSnapshot.CaptureMaterialAsync(library, snapshot, material.Id, cancellationToken).ConfigureAwait(false);
            }

            snapshot.Projects.Add(new Project
            {
                Name = name.Trim(),
                CreatedAt = now,
                ModifiedAt = now,
                MachineProfileId = machineId,
                MaterialId = material?.Id,
                Goal = goal,
                Tolerances = Tolerances.ForScale(material?.ToleranceScale ?? 1),
            });
            snapshot.Stocks.Add(new Stock { Type = StockType.Box, SizeX = 100, SizeY = 100, SizeZ = 25 });
            snapshot.Setups.Add(new Setup
            {
                Order = 1,
                Name = "Setup 1",
                Orientation = SetupOrientation.ThreeAxis,
                ProbeMethod = machine.Probe.DefaultMethod,
            });
            await snapshot.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return await OpenAsync(folder, libraryPath, time, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProjectSession> OpenAsync(string folder, string libraryPath, TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        folder = Path.GetFullPath(folder);
        if (!IsProjectFolder(folder))
        {
            throw new FileNotFoundException($"'{folder}' does not contain a 3C Tool project ({ProjectDatabase.FileName}).");
        }

        var db = await ProjectDatabase.OpenAsync(folder, cancellationToken).ConfigureAwait(false);
        try
        {
            var project = await db.Projects.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The project database has no project.");
            var stock = await db.Stocks.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (stock is null)
            {
                stock = new Stock { Type = StockType.Box, SizeX = 100, SizeY = 100, SizeZ = 25 };
                db.Stocks.Add(stock);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var partList = await db.PartModels.ToListAsync(cancellationToken).ConfigureAwait(false);
            var zones = await db.KeepOutZones.ToListAsync(cancellationToken).ConfigureAwait(false);
            var setupList = await db.Setups.Include(s => s.Operations).ToListAsync(cancellationToken).ConfigureAwait(false);
            var tools = await db.ProjectTools.ToListAsync(cancellationToken).ConfigureAwait(false);
            var session = new ProjectSession(folder, libraryPath, db, time ?? TimeProvider.System, project, stock, partList, zones,
                [.. setupList.OrderBy(s => s.Order)], tools);
            foreach (var setup in session.setups)
            {
                setup.Operations.Sort((a, b) => a.Order.CompareTo(b.Order));
            }

            await session.ReloadSnapshotsAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Saves pending entity changes and raises <see cref="Changed"/>.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Project.ModifiedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Tool? FindTool(Guid id) => Tools.FirstOrDefault(t => t.Id == id);

    /// <summary>First collet of the machine whose bore matches the tool shank (or diameter when no shank is given).</summary>
    public Collet? ColletFor(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var shank = tool.ShankDiameter > 0 ? tool.ShankDiameter : tool.Diameter;
        return Machine.Collets.FirstOrDefault(c => Math.Abs(c.ShankDiameter - shank) < 1e-6);
    }

    // ----- Library snapshots -----

    public async Task<IReadOnlyList<SnapshotDifference>> CompareWithLibraryAsync(CancellationToken cancellationToken = default)
    {
        await using var library = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(true);
        return await ProjectSnapshot.CompareAsync(library, db, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Re-captures one changed snapshot from the library.</summary>
    public async Task RefreshSnapshotAsync(SnapshotDifference difference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(difference);
        if (difference.Kind == SnapshotDifferenceKind.MissingFromLibrary)
        {
            throw new InvalidOperationException($"{difference.Name} no longer exists in the library; the project keeps its copy.");
        }

        await CaptureAsync(async (library, snapshot) =>
        {
            switch (difference.EntityType)
            {
                case nameof(MachineProfile):
                    await ProjectSnapshot.CaptureMachineAsync(library, snapshot, difference.Id, cancellationToken).ConfigureAwait(false);
                    break;
                case nameof(Tool):
                    await ProjectSnapshot.CaptureToolsAsync(library, snapshot, [difference.Id], cancellationToken).ConfigureAwait(false);
                    break;
                case nameof(Material):
                    await ProjectSnapshot.CaptureMaterialAsync(library, snapshot, difference.Id, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"{difference.EntityType} snapshots are not used by milling projects.");
            }
        }, cancellationToken).ConfigureAwait(true);
    }

    public async Task SetMachineAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        await CaptureAsync((library, snapshot) => ProjectSnapshot.CaptureMachineAsync(library, snapshot, machineId, cancellationToken), cancellationToken).ConfigureAwait(true);
        Project.MachineProfileId = machineId;
        await SaveAsync(cancellationToken).ConfigureAwait(true);
        await ReloadSnapshotsAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Changes the material; <paramref name="rescaleTolerances"/> resets the tolerances to the material's scale.</summary>
    public async Task SetMaterialAsync(Guid materialId, bool rescaleTolerances, CancellationToken cancellationToken = default)
    {
        await CaptureAsync((library, snapshot) => ProjectSnapshot.CaptureMaterialAsync(library, snapshot, materialId, cancellationToken), cancellationToken).ConfigureAwait(true);
        Project.MaterialId = materialId;
        await ReloadSnapshotsAsync(cancellationToken).ConfigureAwait(true);
        if (rescaleTolerances && Material is not null)
        {
            CopyTolerances(Tolerances.ForScale(Material.ToleranceScale), Project.Tolerances);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task AddToolsAsync(IReadOnlyCollection<Guid> toolIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var added = toolIds.Where(id => projectTools.TrueForAll(t => t.ToolId != id)).Distinct().ToList();
        if (added.Count == 0)
        {
            return;
        }

        await CaptureAsync((library, snapshot) => ProjectSnapshot.CaptureToolsAsync(library, snapshot, added, cancellationToken), cancellationToken).ConfigureAwait(true);
        foreach (var id in added)
        {
            var link = new ProjectTool { ToolId = id };
            db.ProjectTools.Add(link);
            projectTools.Add(link);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(true);
        await ReloadSnapshotsAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task RemoveToolAsync(Guid toolId, CancellationToken cancellationToken = default)
    {
        var users = setups.SelectMany(s => s.Operations).Where(o => o.ToolId == toolId).Select(o => o.Name).ToList();
        if (users.Count > 0)
        {
            throw new InvalidOperationException($"The tool is used by {string.Join(", ", users)}.");
        }

        foreach (var link in projectTools.Where(t => t.ToolId == toolId).ToList())
        {
            db.ProjectTools.Remove(link);
            projectTools.Remove(link);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(true);
        await ReloadSnapshotsAsync(cancellationToken).ConfigureAwait(true);
    }

    // ----- Models -----

    /// <summary>
    /// Copies a model file into <c>models/</c>, records its SHA-256, repairs its bodies and adds it to the project.
    /// STL files carry no unit: <paramref name="unit"/> is what the user chose (3MF files declare theirs).
    /// </summary>
    public async Task<PartImport> ImportPartAsync(string sourcePath, LengthUnit unit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        var (bodies, declared, sha) = await Task.Run(() =>
        {
            var imported = MeshImporter.Load(sourcePath);
            return (imported.Bodies, imported.UnitsDeclared, imported.Sha256);
        }, cancellationToken).ConfigureAwait(true);
        if (bodies.Count == 0 || bodies.All(b => b.Mesh.TriangleCount == 0))
        {
            throw new InvalidDataException("The file contains no triangles.");
        }

        var relative = await CopyIntoProjectAsync(sourcePath, sha, cancellationToken).ConfigureAwait(true);
        var (repaired, report) = await Task.Run(() => PartGeometry.Repair(bodies), cancellationToken).ConfigureAwait(true);
        var model = new PartModel
        {
            Name = Path.GetFileNameWithoutExtension(sourcePath),
            FilePath = relative,
            Sha256 = sha,
            ImportUnit = declared ? LengthUnit.Millimeter : unit,
            Role = bodies.All(b => b.Role == BodyRole.Print) ? BodyRole.Print : BodyRole.Mill,
            RepairReport = report,
        };
        db.PartModels.Add(model);
        parts.Add(model);
        var part = new LoadedPart(model, repaired, repaired.Aggregate(BoundingBox.Empty, (box, b) => box.Union(b.Mesh.Bounds)));
        loaded[model.Id] = part;
        await SaveAsync(cancellationToken).ConfigureAwait(true);
        return new PartImport(model, part, declared);
    }

    public async Task RemovePartAsync(PartModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        db.PartModels.Remove(model);
        parts.Remove(model);
        loaded.Remove(model.Id);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
        if (parts.TrueForAll(p => p.FilePath != model.FilePath) && Stock.MeshPath != model.FilePath)
        {
            TryDelete(PartGeometry.Resolve(Folder, model.FilePath));
        }
    }

    /// <summary>Loaded (and repaired) bodies of a model, cached for the session.</summary>
    public async Task<LoadedPart> LoadPartAsync(PartModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (loaded.TryGetValue(model.Id, out var part))
        {
            return part with { Model = model };
        }

        var folder = Folder;
        var copy = EntityCopy.Clone(model);
        part = await Task.Run(() => PartGeometry.Load(folder, copy), cancellationToken).ConfigureAwait(true);
        part = part with { Model = model };
        loaded[model.Id] = part;
        return part;
    }

    /// <summary>All milled bodies of all models, merged in the part frame.</summary>
    public async Task<Mesh> MillPartAsync(CancellationToken cancellationToken = default)
    {
        var meshes = new List<Mesh>();
        foreach (var model in parts.Where(p => p.Role == BodyRole.Mill))
        {
            meshes.Add((await LoadPartAsync(model, cancellationToken).ConfigureAwait(true)).MillMesh());
        }

        return Mesh.Merge(meshes.Where(m => m.TriangleCount > 0), "part");
    }

    public async Task SetImportUnitAsync(PartModel model, LengthUnit unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.ImportUnit = unit;
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task AddTransformAsync(PartModel model, TransformStep step, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(step);
        _ = MeshTransforms.ToMatrix(step);
        model.Transforms.Add(step);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Removes the last transform step; returns false when the history is empty.</summary>
    public async Task<bool> UndoTransformAsync(PartModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Transforms.Count == 0)
        {
            return false;
        }

        model.Transforms.RemoveAt(model.Transforms.Count - 1);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
        return true;
    }

    public async Task ClearTransformsAsync(PartModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Transforms.Clear();
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Moves the model onto the box stock (centred in XY, top/bottom/centre aligned in Z).</summary>
    public async Task PlaceOnStockAsync(PartModel model, StockAnchor anchor, CancellationToken cancellationToken = default)
    {
        var part = await LoadPartAsync(model, cancellationToken).ConfigureAwait(true);
        var offset = Stock.Type == StockType.Cylinder
            ? Placement.OnRotaryAxis(part.AllBodies(), Stock)
            : Placement.OnBoxStock(part.AllBodies().Bounds, Stock, anchor);
        if (offset.LengthSquared() > 1e-12f)
        {
            await AddTransformAsync(model, MeshTransforms.Translate(offset), cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task<IReadOnlyList<OrientationCandidate>> SuggestOrientationsAsync(PartModel model, MachiningMode mode, CancellationToken cancellationToken = default)
    {
        var mesh = (await LoadPartAsync(model, cancellationToken).ConfigureAwait(true)).AllBodies();
        return await Task.Run(() => OrientationAdvisor.Suggest(mesh, mode), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Applies an orientation suggestion as a rotation step about the model's current centre.</summary>
    public async Task ApplyOrientationAsync(PartModel model, OrientationCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (PartGeometry.RotationStep(candidate.Rotation) is { } rotation)
        {
            await RotateAboutCenterAsync(model, rotation, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Rotation about the model's bounding-box centre (translate to origin, rotate, translate back).</summary>
    public async Task RotateAboutCenterAsync(PartModel model, TransformStep rotation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        var center = (await LoadPartAsync(model, cancellationToken).ConfigureAwait(true)).AllBodies().Bounds.Center;
        model.Transforms.Add(MeshTransforms.Translate(-center));
        model.Transforms.Add(rotation);
        model.Transforms.Add(MeshTransforms.Translate(center));
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    // ----- Stock -----

    /// <summary>Copies a stock mesh file into the project and switches the stock to it.</summary>
    public async Task SetStockMeshAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var sha = await Task.Run(() =>
        {
            var imported = MeshImporter.Load(sourcePath);
            return imported.Bodies.Sum(b => b.Mesh.TriangleCount) == 0 ? throw new InvalidDataException("The file contains no triangles.") : imported.Sha256;
        }, cancellationToken).ConfigureAwait(true);
        Stock.MeshPath = await CopyIntoProjectAsync(sourcePath, sha, cancellationToken).ConfigureAwait(true);
        Stock.MeshSha256 = sha;
        Stock.Type = StockType.Mesh;
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    public Mesh? LoadStockMesh() => PartGeometry.LoadStockMesh(Folder, Stock);

    /// <summary>Stock solid in the part frame.</summary>
    public Mesh? StockSolid() => PartGeometry.StockMesh(Stock, LoadStockMesh());

    // ----- Keep-out zones -----

    public async Task<KeepOutZone> AddKeepOutAsync(KeepOutZone zone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ValidateKeepOut(zone);
        db.KeepOutZones.Add(zone);
        keepOuts.Add(zone);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
        return zone;
    }

    public async Task RemoveKeepOutAsync(KeepOutZone zone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zone);
        db.KeepOutZones.Remove(zone);
        keepOuts.Remove(zone);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    public static void ValidateKeepOut(KeepOutZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var s = zone.Solid;
        if (s.Height <= 0 || (s.Shape == SolidShape.Box ? s.SizeX <= 0 || s.SizeY <= 0 : s.Radius <= 0))
        {
            throw new InvalidOperationException("Keep-out zones need a positive height and size.");
        }

        if (zone.Attachment != KeepOutAttachment.Machine && zone.SetupId is null)
        {
            throw new InvalidOperationException("Setup keep-out zones must name their setup.");
        }
    }

    // ----- Setups and operations -----

    public async Task<Setup> AddSetupAsync(SetupOrientation orientation, CancellationToken cancellationToken = default)
    {
        var setup = new Setup
        {
            Order = setups.Count == 0 ? 1 : setups.Max(s => s.Order) + 1,
            Name = $"Setup {setups.Count + 1}",
            Orientation = orientation,
            WcsReference = orientation == SetupOrientation.ThreeAxis ? WcsReference.StockTopCenter : WcsReference.RotaryAxis,
            ProbeMethod = Machine.Probe.DefaultMethod,
            WorkOffset = WorkOffsetFor(setups.Count),
            RotaryCenterName = orientation == SetupOrientation.ThreeAxis ? null : Machine.RotaryAxis.Centers.FirstOrDefault()?.Name,
        };
        db.Setups.Add(setup);
        setups.Add(setup);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
        return setup;
    }

    public async Task RemoveSetupAsync(Setup setup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var operations = setup.Operations.Select(o => o.Id).ToList();
        await DeleteToolpathResultsAsync(operations, cancellationToken).ConfigureAwait(true);
        foreach (var zone in keepOuts.Where(k => k.SetupId == setup.Id).ToList())
        {
            db.KeepOutZones.Remove(zone);
            keepOuts.Remove(zone);
        }

        db.Setups.Remove(setup);
        setups.Remove(setup);
        Renumber(setups, (s, i) => s.Order = i);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task MoveSetupAsync(Setup setup, int delta, CancellationToken cancellationToken = default)
    {
        if (Move(setups, setup, delta))
        {
            Renumber(setups, (s, i) => s.Order = i);
            await SaveAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task<Operation> AddOperationAsync(Setup setup, StrategyType strategy, Guid toolId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (FindTool(toolId) is null)
        {
            throw new InvalidOperationException("Add the tool to the project first.");
        }

        var operation = new Operation
        {
            SetupId = setup.Id,
            Order = setup.Operations.Count == 0 ? 1 : setup.Operations.Max(o => o.Order) + 1,
            Name = $"{Describe(strategy)} {setups.Sum(s => s.Operations.Count(o => o.Strategy == strategy)) + 1}",
            Strategy = strategy,
            ToolId = toolId,
        };
        setup.Operations.Add(operation);
        db.Operations.Add(operation);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
        return operation;
    }

    public async Task RemoveOperationAsync(Operation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var setup = setups.Single(s => s.Id == operation.SetupId);
        await DeleteToolpathResultsAsync([operation.Id], cancellationToken).ConfigureAwait(true);
        setup.Operations.Remove(operation);
        db.Operations.Remove(operation);
        Renumber(setup.Operations, (o, i) => o.Order = i);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task MoveOperationAsync(Operation operation, int delta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var setup = setups.Single(s => s.Id == operation.SetupId);
        if (Move(setup.Operations, operation, delta))
        {
            Renumber(setup.Operations, (o, i) => o.Order = i);
            await SaveAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Copy of an operation (new id, same parameters) placed after it.</summary>
    public async Task<Operation> DuplicateOperationAsync(Operation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var setup = setups.Single(s => s.Id == operation.SetupId);
        var copy = EntityCopy.Clone(operation);
        copy.Id = Guid.CreateVersion7();
        copy.Name = operation.Name + " copy";
        copy.Conditions = null;
        setup.Operations.Insert(setup.Operations.IndexOf(operation) + 1, copy);
        db.Operations.Add(copy);
        Renumber(setup.Operations, (o, i) => o.Order = i);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
        return copy;
    }

    public static string Describe(StrategyType strategy) => strategy switch
    {
        StrategyType.AdaptiveRoughing => "Adaptive roughing",
        StrategyType.ZLevelRoughing => "Z-level roughing",
        StrategyType.ParallelFinishing => "Parallel finishing",
        StrategyType.Waterline => "Waterline",
        StrategyType.Scallop => "Scallop",
        StrategyType.RestMachining => "Rest machining",
        StrategyType.Pencil => "Pencil",
        StrategyType.RotaryRoughing => "Rotary roughing",
        StrategyType.RotaryFinishing => "Rotary finishing",
        _ => strategy.ToString(),
    };

    // ----- Results -----

    /// <summary>Stored result of an operation (<see cref="SaveToolpathResultAsync"/> keeps one per operation).</summary>
    public async Task<ToolpathResult?> GetToolpathResultAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        await db.ToolpathResults.AsNoTracking().FirstOrDefaultAsync(r => r.OperationId == operationId, cancellationToken).ConfigureAwait(true);

    internal async Task SaveToolpathResultAsync(ToolpathResult result, CancellationToken cancellationToken)
    {
        await db.ToolpathResults.Where(r => r.OperationId == result.OperationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(true);
        db.ToolpathResults.Add(result);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        db.Entry(result).State = EntityState.Detached;
    }

    public async Task<IReadOnlyList<GcodeFile>> ListGcodeFilesAsync(CancellationToken cancellationToken = default)
    {
        var files = await db.GcodeFiles.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(true);
        return [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    public string ToolpathCachePath(Guid operationId) => Path.Combine(Folder, ToolpathsFolder, operationId.ToString("N") + ".3ctp");

    public async ValueTask DisposeAsync()
    {
        await db.DisposeAsync().ConfigureAwait(false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    internal static void CopyTolerances(Tolerances from, Tolerances to)
    {
        to.RoughingTolerance = from.RoughingTolerance;
        to.FinishingTolerance = from.FinishingTolerance;
        to.ArcFittingTolerance = from.ArcFittingTolerance;
        to.RoughingStockToLeave = from.RoughingStockToLeave;
        to.SemiFinishStockToLeave = from.SemiFinishStockToLeave;
        to.FinishingStockToLeave = from.FinishingStockToLeave;
        to.MaxScallopHeight = from.MaxScallopHeight;
        to.GougeThreshold = from.GougeThreshold;
    }

    private static WorkOffset WorkOffsetFor(int index) => (WorkOffset)Math.Min(index, (int)WorkOffset.G58);

    private static bool Move<T>(List<T> list, T item, int delta)
    {
        var index = list.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count || delta == 0)
        {
            return false;
        }

        list.RemoveAt(index);
        list.Insert(target, item);
        return true;
    }

    private static void Renumber<T>(List<T> list, Action<T, int> set)
    {
        for (var i = 0; i < list.Count; i++)
        {
            set(list[i], i + 1);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task DeleteToolpathResultsAsync(List<Guid> operations, CancellationToken cancellationToken)
    {
        if (operations.Count == 0)
        {
            return;
        }

        await db.ToolpathResults.Where(r => operations.Contains(r.OperationId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(true);
        foreach (var id in operations)
        {
            TryDelete(ToolpathCachePath(id));
        }
    }

    private async Task<string> CopyIntoProjectAsync(string sourcePath, string sha, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Folder, ModelsFolder);
        Directory.CreateDirectory(directory);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        for (var n = 1; ; n++)
        {
            var file = n == 1 ? name + extension : $"{name}_{n}{extension}";
            var target = Path.Combine(directory, file);
            if (File.Exists(target))
            {
                if (string.Equals(MeshImporter.ComputeSha256(target), sha, StringComparison.OrdinalIgnoreCase))
                {
                    return ModelsFolder + "/" + file;
                }

                continue;
            }

            await using (var source = File.OpenRead(sourcePath))
            await using (var destination = File.Create(target + ".tmp"))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(true);
            }

            File.Move(target + ".tmp", target);
            if (!string.Equals(MeshImporter.ComputeSha256(target), sha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(target);
                throw new IOException("The model file changed while it was being copied.");
            }

            return ModelsFolder + "/" + file;
        }
    }

    private async Task CaptureAsync(Func<LibraryDbContext, ProjectDbContext, Task> capture, CancellationToken cancellationToken)
    {
        // Captures clear their context's change tracker, so they get their own context.
        await using var library = await LibraryDatabase.OpenAsync(LibraryPath, cancellationToken).ConfigureAwait(true);
        await using var snapshot = await ProjectDatabase.OpenAsync(Folder, cancellationToken).ConfigureAwait(true);
        await capture(library, snapshot).ConfigureAwait(true);
        await ReloadSnapshotsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ReloadSnapshotsAsync(CancellationToken cancellationToken)
    {
        Machine = await db.MachineProfiles.AsNoTracking().Include(m => m.Collets).Include(m => m.Toolheads)
            .SingleOrDefaultAsync(m => m.Id == Project.MachineProfileId, cancellationToken).ConfigureAwait(true)
            ?? throw new InvalidDataException("The project's machine snapshot is missing.");
        Material = Project.MaterialId is { } materialId
            ? await db.Materials.AsNoTracking().SingleOrDefaultAsync(m => m.Id == materialId, cancellationToken).ConfigureAwait(true)
            : null;
        var ids = projectTools.Select(t => t.ToolId).ToList();
        var tools = await db.Tools.AsNoTracking().Where(t => ids.Contains(t.Id)).ToListAsync(cancellationToken).ConfigureAwait(true);
        Tools = [.. tools.OrderBy(t => t.Diameter).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
