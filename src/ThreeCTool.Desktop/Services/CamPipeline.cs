using System.Security.Cryptography;

using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Data;
using ThreeCTool.Feeds;
using ThreeCTool.Geometry;
using ThreeCTool.Post;
using ThreeCTool.Simulation;
using ThreeCTool.Toolpaths;

using Microsoft.EntityFrameworkCore;

namespace ThreeCTool.Desktop.Services;

/// <summary>Operations of one G-code file: one tool in one setup, in machining order.</summary>
public sealed record CamGroup(Setup Setup, Tool Tool, IReadOnlyList<Operation> Operations, bool FirstInSetup, int FileIndex);

/// <summary>Progress of a pipeline stage.</summary>
public readonly record struct CamProgress(string Stage, string Item, double Fraction);

/// <summary>Toolpath of an operation for a stock piece (0 when the stock is not split) and the inputs it was generated from.</summary>
public sealed record GeneratedToolpath(Operation Operation, string InputHash, Toolpath Toolpath, bool FromCache, int Piece = 0);

/// <summary>Feeds of one file.</summary>
public sealed record CamFeedPlan(CamGroup Group, ToolGroupPlan Plan);

/// <summary>Simulation of one stock piece (piece 0 when the stock is not split).</summary>
public sealed record PieceSimulation(StockPiece Piece, SimulationResult Result);

/// <summary>
/// Generate → feeds → simulate → post for an open project. Called from the UI thread (it reads and writes the
/// session); the heavy work runs on the thread pool with detached copies. Toolpaths are cached in
/// <c>toolpaths/</c> keyed by a hash of every input, so only operations whose inputs changed are regenerated.
/// A box stock split into standard pieces (<see cref="StockTiling"/>) is machined piece by piece: every piece gets its
/// own toolpaths, simulation and G-code files, with the part clipped at the seams.
/// </summary>
public sealed class CamPipeline : IDisposable
{
    /// <summary>Bumped whenever generated output for identical inputs changes.</summary>
    public static readonly string GeneratorVersion = typeof(ToolpathGenerator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private const int MaxPieceParts = 256;

    /// <summary>Gap (mm) between the tool and the stock needed to offer the touch plate on the bed at X0 Y0.</summary>
    private const double BedSpotMargin = 5;

    private readonly ProjectSession session;
    private readonly ComputeService compute;
    private readonly TimeProvider time;
    private readonly Dictionary<(Guid Operation, int Piece), GeneratedToolpath> toolpaths = [];
    private readonly Dictionary<Guid, EngagementLimiter> limiters = [];
    private readonly List<CamFeedPlan> feedPlans = [];
    private readonly Dictionary<string, Mesh> pieceParts = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (PieceSimulation Simulation, SimulationJob Job)> simulations = [];
    private readonly List<int> emptyPieces = [];
    private string? simulationFingerprint;
    private int activePiece;

    public CamPipeline(ProjectSession session, ComputeService compute, TimeProvider? time = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.compute = compute ?? throw new ArgumentNullException(nameof(compute));
        this.time = time ?? TimeProvider.System;
        compute.Resetting += OnComputeResetting;
    }

    public IReadOnlyList<CamFeedPlan> FeedPlans => feedPlans;

    /// <summary>Stock pieces in machining order; one piece numbered 0 when the stock is not split.</summary>
    public IReadOnlyList<StockPiece> Pieces => StockTiling.Pieces(session.Stock);

    /// <summary>The stock is split into several pieces.</summary>
    public bool IsTiled => StockTiling.IsTiled(session.Stock);

    /// <summary>
    /// Piece shown by <see cref="GetToolpath(Guid)"/>, <see cref="Simulation"/> and playback; falls back to the first
    /// piece that holds part of the model.
    /// </summary>
    public int ActivePiece
    {
        get
        {
            var pieces = Pieces.Where(p => !emptyPieces.Contains(p.Number)).DefaultIfEmpty(Pieces[0]).ToList();
            return pieces.Any(p => p.Number == activePiece) ? activePiece : pieces[0].Number;
        }

        set => activePiece = value;
    }

    /// <summary>Pieces that held none of the part when toolpaths were last generated; they are not machined.</summary>
    public IReadOnlyList<int> EmptyPieces => emptyPieces;

    /// <summary>Last simulation of the active piece (stock and verification report), null before the first run.</summary>
    public SimulationResult? Simulation => simulations.TryGetValue(ActivePiece, out var s) ? s.Simulation.Result : null;

    /// <summary>Last simulation of every machined piece, in machining order.</summary>
    public IReadOnlyList<PieceSimulation> Simulations => [.. simulations.Values.Select(s => s.Simulation).OrderBy(s => s.Piece.Number)];

    /// <summary>True when the project changed since <see cref="Simulations"/> were computed.</summary>
    public bool SimulationIsStale => simulations.Count == 0 || simulationFingerprint != Fingerprint();

    /// <summary>Up-to-date toolpath of an operation for the active piece.</summary>
    public GeneratedToolpath? GetToolpath(Guid operationId) => GetToolpath(operationId, ActivePiece);

    public GeneratedToolpath? GetToolpath(Guid operationId, int piece) =>
        toolpaths.TryGetValue((operationId, piece), out var generated) && generated.InputHash == InputHash(generated.Operation, piece) ? generated : null;

    /// <summary>
    /// Why the project cannot be machined with its stock pieces (invalid seams, setups that are not 3-axis, turned
    /// stock or tabs on a split stock) or its stock turns (<see cref="StockTurn.Problems"/>), or empty.
    /// </summary>
    public IReadOnlyList<string> TilingProblems()
    {
        var problems = StockTiling.Problems(session.Stock).ToList();
        var active = session.Setups.Where(s => s.Operations.Any(o => o.Enabled)).ToList();
        if (IsTiled)
        {
            problems.AddRange(active
                .Where(s => s.Orientation != SetupOrientation.ThreeAxis)
                .Select(s => $"Setup '{s.Name}' is not 3-axis; a stock split into pieces can only be machined with 3-axis setups."));
            problems.AddRange(active
                .Where(s => StockTurn.IsTurned(s) || (s.Orientation == SetupOrientation.ThreeAxis && s.TabCount > 0))
                .Select(s => $"Setup '{s.Name}' turns the stock or has tabs; a stock split into pieces is only machined from the top."));
        }

        problems.AddRange(StockTurn.Problems(session.Stock, session.Setups));
        return problems;
    }

    // ----- Plan -----

    /// <summary>Files in machining order: setups by order; within a setup, tools by first use; operations by order.</summary>
    public IReadOnlyList<CamGroup> Plan()
    {
        var groups = new List<CamGroup>();
        foreach (var setup in session.Setups)
        {
            var index = 0;
            var enabled = setup.Operations.Where(o => o.Enabled).ToList();
            foreach (var toolId in enabled.Select(o => o.ToolId).Distinct())
            {
                var tool = session.FindTool(toolId) ?? throw new InvalidOperationException($"Operation '{enabled.First(o => o.ToolId == toolId).Name}' uses a tool that is not in the project.");
                index++;
                groups.Add(new CamGroup(setup, tool, [.. enabled.Where(o => o.ToolId == toolId)], index == 1, index));
            }
        }

        return groups;
    }

    /// <summary>Enabled operations in machining order.</summary>
    public IReadOnlyList<Operation> ExecutionOrder() => [.. Plan().SelectMany(g => g.Operations)];

    /// <summary>Up-to-date toolpaths of the active piece in machining order (operations without one are skipped).</summary>
    public IReadOnlyList<GeneratedToolpath> CurrentToolpaths() => [.. ExecutionOrder().Select(o => GetToolpath(o.Id)).OfType<GeneratedToolpath>()];

    /// <summary>Hash of everything a toolpath of the active piece depends on, including the operations machined before it.</summary>
    public string InputHash(Operation operation) => InputHash(operation, ActivePiece);

    public string InputHash(Operation operation, int piece)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var stockPiece = PieceOf(piece);
        var order = ExecutionOrder();
        var hashes = new Dictionary<Guid, string>();
        foreach (var op in order)
        {
            hashes[op.Id] = Hash(op, [.. hashes.Values], stockPiece);
            if (op.Id == operation.Id)
            {
                return hashes[op.Id];
            }
        }

        return Hash(operation, [.. hashes.Values], stockPiece);
    }

    // ----- Generate -----

    /// <summary>Generates (or loads from the cache) the toolpath of every enabled operation for every stock piece.</summary>
    public async Task<IReadOnlyList<GeneratedToolpath>> GenerateAsync(IProgress<CamProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureMachinable();
        var order = ExecutionOrder();
        if (order.Count == 0)
        {
            throw new InvalidOperationException("The project has no enabled operations.");
        }

        var part = await session.MillPartAsync(cancellationToken).ConfigureAwait(true);
        if (part.TriangleCount == 0)
        {
            throw new InvalidOperationException("Import a part to mill first.");
        }

        var pieces = await PiecePartsAsync(part, cancellationToken).ConfigureAwait(true);
        var keepOuts = session.KeepOuts.Select(EntityCopy.Clone).ToList();
        var tolerances = EntityCopy.Clone(session.Project.Tolerances);
        var machine = session.Machine;
        var results = new List<GeneratedToolpath>();
        var total = order.Count * pieces.Count;
        using var lease = await compute.AcquireAsync(cancellationToken).ConfigureAwait(true);
        foreach (var (piece, piecePart) in pieces)
        {
            var stock = PieceStock(piece);
            var stockMesh = piece.Number == 0 ? session.LoadStockMesh() : null;
            var layouts = new Dictionary<Guid, TabLayout?>();
            var previous = new List<PreviousCut>();
            var hashes = new List<string>();
            for (var i = 0; i < order.Count; i++)
            {
                var operation = order[i];
                var tool = session.FindTool(operation.ToolId)!;
                var hash = Hash(operation, hashes, piece);
                hashes.Add(hash);
                var item = piece.Number == 0 ? operation.Name : $"{piece.Label}: {operation.Name}";
                var done = results.Count;
                progress?.Report(new CamProgress("Toolpaths", item, (double)done / total));
                var generated = await LoadCachedAsync(operation, piece.Number, hash, cancellationToken).ConfigureAwait(true);
                if (generated is null)
                {
                    var setup = SetupCopy(operation.SetupId);
                    var side = await Task.Run(() => SideOf(setup, stock, piecePart, layouts), cancellationToken).ConfigureAwait(true);
                    var request = new ToolpathRequest
                    {
                        Compute = lease.Context,
                        Operation = EntityCopy.Clone(operation),
                        Setup = setup,
                        Tool = tool,
                        Collet = session.ColletFor(tool),
                        Stock = stock,
                        StockMesh = stockMesh,
                        Part = piecePart,
                        KeepOuts = keepOuts,
                        Tolerances = tolerances,
                        Machine = machine,
                        PreviousCuts = [.. previous],
                        DepthLimit = side.Depth,
                        Tabs = side.Tabs,
                        SetupWarnings = side.Warnings,
                    };
                    var inner = progress is null ? null : new Progress<double>(f => progress.Report(new CamProgress("Toolpaths", item, (done + f) / total)));
                    var toolpath = await Task.Run(() => ToolpathGenerator.Generate(request, inner, cancellationToken), cancellationToken).ConfigureAwait(true);
                    generated = new GeneratedToolpath(operation, hash, toolpath, false, piece.Number);
                    await StoreAsync(generated, cancellationToken).ConfigureAwait(true);
                }

                toolpaths[(operation.Id, piece.Number)] = generated;
                results.Add(generated);
                previous.Add(new PreviousCut(generated.Toolpath, tool));
            }
        }

        progress?.Report(new CamProgress("Toolpaths", string.Empty, 1));
        return results;
    }

    /// <summary>Loads the cached toolpaths whose inputs are unchanged, without generating any; returns how many are current.</summary>
    public async Task<int> LoadCacheAsync(CancellationToken cancellationToken = default)
    {
        var current = 0;
        var order = ExecutionOrder();
        foreach (var piece in Pieces)
        {
            var hashes = new List<string>();
            foreach (var operation in order)
            {
                var hash = Hash(operation, hashes, piece);
                hashes.Add(hash);
                if (await LoadCachedAsync(operation, piece.Number, hash, cancellationToken).ConfigureAwait(true) is { } generated)
                {
                    toolpaths[(operation.Id, piece.Number)] = generated;
                    current++;
                }
            }
        }

        return current;
    }

    // ----- Feeds -----

    /// <summary>Plans the feeds of every file (one RPM per file) and stores the conditions on the operations.</summary>
    public async Task<IReadOnlyList<CamFeedPlan>> PlanFeedsAsync(CancellationToken cancellationToken = default)
    {
        var material = session.Material ?? throw new InvalidOperationException("Choose the project material first.");
        var corrections = await LoadCorrectionsAsync(material, cancellationToken).ConfigureAwait(true);
        var engine = new FeedsEngine(new FeedContext
        {
            Machine = session.Machine,
            Material = material,
            Goal = session.Project.Goal,
            Tolerances = EntityCopy.Clone(session.Project.Tolerances),
            Corrections = corrections,
        });

        var plans = new List<CamFeedPlan>();
        var pieces = Pieces;
        double? incoming = null;
        foreach (var group in Plan())
        {
            var requests = new List<FeedRequest>();
            foreach (var operation in group.Operations)
            {
                var settings = ToolpathSettings.Resolve(operation, group.Tool, session.Project.Tolerances);
                requests.Add(new FeedRequest
                {
                    OperationId = operation.Id,
                    Strategy = operation.Strategy,
                    Tool = group.Tool,
                    Stepover = settings.Stepover,
                    Stepdown = settings.Stepdown,
                    Tolerance = settings.Tolerance,
                    RadialStock = settings.RadialStock,
                    IncomingStock = incoming,
                    CutLength = pieces.Sum(p => GetToolpath(operation.Id, p.Number)?.Toolpath.CutLength ?? 0),
                });
                incoming = settings.RadialStock;
            }

            var plan = engine.Plan(requests);
            foreach (var result in plan.Operations)
            {
                var operation = group.Operations.Single(o => o.Id == result.Request.OperationId);
                operation.Conditions = result.ToConditions();
                limiters[operation.Id] = result.Limiter;
            }

            plans.Add(new CamFeedPlan(group, plan));
        }

        await session.SaveAsync(cancellationToken).ConfigureAwait(true);
        feedPlans.Clear();
        feedPlans.AddRange(plans);
        return plans;
    }

    // ----- Simulate -----

    /// <summary>
    /// Simulates every enabled operation on the dexel stock of every machined piece and verifies the result; returns
    /// the simulation of the active piece.
    /// </summary>
    public async Task<SimulationResult> SimulateAsync(IProgress<CamProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureMachinable();
        var jobs = await CreateJobsAsync(progress, cancellationToken).ConfigureAwait(true);
        var fingerprint = Fingerprint();
        using var lease = await compute.AcquireAsync(cancellationToken).ConfigureAwait(true);

        // Release the previous stocks first: a split stock holds one simulated stock per piece.
        ResetSimulation();
        var results = new Dictionary<int, (PieceSimulation Simulation, SimulationJob Job)>();
        try
        {
            for (var k = 0; k < jobs.Count; k++)
            {
                var (piece, created) = jobs[k];
                var job = WithCompute(created, lease.Context);
                var item = piece.Number == 0 ? string.Empty : piece.Label;
                var index = k;
                var inner = progress is null ? null : new Progress<double>(f => progress.Report(new CamProgress("Simulation", item, (index + f) / jobs.Count)));
                var result = await Task.Run(() => Simulator.Run(job, inner, cancellationToken), cancellationToken).ConfigureAwait(true);
                results[piece.Number] = (new PieceSimulation(piece, result), job);
            }
        }
        catch
        {
            foreach (var (simulation, _) in results.Values)
            {
                simulation.Result.Dispose();
            }

            throw;
        }

        foreach (var (number, entry) in results)
        {
            simulations[number] = entry;
        }

        simulationFingerprint = fingerprint;
        return Simulation ?? results.Values.First().Simulation.Result;
    }

    /// <summary>Step-by-step playback of the last simulation of the active piece (seek with <see cref="SeekAsync"/>).</summary>
    public async Task<SimulationSession> CreatePlaybackAsync(CancellationToken cancellationToken = default)
    {
        var job = simulations.TryGetValue(ActivePiece, out var entry) ? entry.Job : throw new InvalidOperationException("Run the simulation first.");
        using var lease = await compute.AcquireAsync(cancellationToken).ConfigureAwait(true);
        job = WithCompute(job, lease.Context);
        return await Task.Run(() => SimulationSession.Create(job), cancellationToken).ConfigureAwait(true);
    }

    public async Task SeekAsync(SimulationSession playback, int step, int move, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(playback);
        using var lease = await compute.AcquireAsync(cancellationToken).ConfigureAwait(true);
        await Task.Run(() => playback.Seek(step, move), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Surface points of a simulated stock (part frame), for display.</summary>
    public async Task<System.Numerics.Vector3[]> SurfacePointsAsync(Compute.DexelStock stock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stock);
        using var lease = await compute.AcquireAsync(cancellationToken).ConfigureAwait(true);
        return await Task.Run(stock.SurfacePoints, cancellationToken).ConfigureAwait(true);
    }

    // ----- Post -----

    /// <summary>
    /// Posts one G-code file per <see cref="CamGroup"/> (per stock piece when the stock is split) and replaces the
    /// project's previous output. Runs the simulation first when it is missing or stale; errors in the verification
    /// block output unless overridden.
    /// </summary>
    public async Task<IReadOnlyList<PostOutput>> PostAsync(bool overrideVerification = false, IProgress<CamProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureMachinable();
        if (SimulationIsStale)
        {
            await SimulateAsync(progress, cancellationToken).ConfigureAwait(true);
        }

        var simulated = Simulations;
        foreach (var (piece, result) in simulated)
        {
            if (!result.Report.MayWriteOutput(overrideVerification))
            {
                var where = piece.Number == 0 ? string.Empty : $" on {piece.Label.ToLowerInvariant()}";
                throw new PostException($"The simulation found {result.Report.Issues.Count(i => i.Severity == IssueSeverity.Error)} error(s){where}; review the verification report or override it.");
            }
        }

        var pieceCount = Pieces.Count;
        var post = new FluidNcMillingPost();
        var outputs = new List<PostOutput>();
        var groups = Plan();
        var total = groups.Count * simulated.Count;
        foreach (var (piece, simulation) in simulated)
        {
            var report = simulation.Report;
            var stockBounds = piece.Number == 0
                ? (session.StockSolid() ?? throw new InvalidOperationException("The stock mesh is missing.")).Bounds
                : new BoundingBox(new System.Numerics.Vector3((float)piece.MinX, (float)piece.MinY, (float)piece.MinZ), new System.Numerics.Vector3((float)piece.MaxX, (float)piece.MaxY, (float)piece.MaxZ));
            double? stockRadius = piece.Number == 0 && session.Stock.Type == StockType.Cylinder ? session.Stock.Diameter / 2 : null;
            foreach (var group in groups)
            {
                progress?.Report(new CamProgress("Post", piece.Number == 0 ? group.Tool.Name : $"{piece.Label}: {group.Tool.Name}", (double)outputs.Count / total));
                var operations = new List<PostOperation>();
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var operation in group.Operations)
                {
                    var generated = GetToolpath(operation.Id, piece.Number) ?? throw new InvalidOperationException($"Generate the toolpath of '{operation.Name}' first.");
                    var conditions = operation.Conditions ?? throw new InvalidOperationException($"Plan the feeds of '{operation.Name}' first.");
                    operations.Add(new PostOperation(EntityCopy.Clone(operation), generated.Toolpath, EntityCopy.Clone(conditions), report.Steps.Find(s => s.StepId == operation.Id)));
                    hashes[operation.Name] = generated.InputHash;
                }

                var request = new MillingPostRequest
                {
                    Project = EntityCopy.Clone(session.Project),
                    Setup = SetupCopy(group.Setup.Id),
                    Machine = session.Machine,
                    Tool = group.Tool,
                    Collet = session.ColletFor(group.Tool),
                    Material = session.Material,
                    Rpm = group.Operations[0].Conditions!.Rpm,
                    Operations = operations,
                    FirstInSetup = group.FirstInSetup,
                    FileIndex = group.FileIndex,
                    Piece = piece.Number == 0 ? null : piece,
                    PieceCount = pieceCount,
                    TouchPlate = TouchPlateSpot.FromStockBounds(operations[0].Toolpath.Frame, stockBounds, stockRadius, (group.Tool.Diameter / 2) + BedSpotMargin),
                    Verification = report,
                    OverrideVerification = overrideVerification,
                    InputHashes = hashes,
                    GeneratorVersion = GeneratorVersion,
                };
                outputs.Add(await Task.Run(() => post.Post(request), cancellationToken).ConfigureAwait(true));
            }
        }

        await ReplaceOutputAsync(outputs, cancellationToken).ConfigureAwait(true);
        progress?.Report(new CamProgress("Post", string.Empty, 1));
        return outputs;
    }

    /// <summary>Generate, feeds, simulate and post.</summary>
    public async Task<IReadOnlyList<PostOutput>> RunAllAsync(bool overrideVerification = false, IProgress<CamProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await GenerateAsync(progress, cancellationToken).ConfigureAwait(true);
        await PlanFeedsAsync(cancellationToken).ConfigureAwait(true);
        await SimulateAsync(progress, cancellationToken).ConfigureAwait(true);
        return await PostAsync(overrideVerification, progress, cancellationToken).ConfigureAwait(true);
    }

    public void Dispose()
    {
        compute.Resetting -= OnComputeResetting;
        ResetSimulation();
    }

    private static SimulationJob WithCompute(SimulationJob job, Compute.ComputeContext context) => new()
    {
        Compute = context,
        Part = job.Part,
        Stock = job.Stock,
        StockMesh = job.StockMesh,
        Machine = job.Machine,
        Tolerances = job.Tolerances,
        KeepOuts = job.KeepOuts,
        Steps = job.Steps,
        WorkOffsets = job.WorkOffsets,
        Options = job.Options,
    };

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private void OnComputeResetting(object? sender, EventArgs e) => ResetSimulation();

    private void ResetSimulation()
    {
        foreach (var (simulation, _) in simulations.Values)
        {
            simulation.Result.Dispose();
        }

        simulations.Clear();
        simulationFingerprint = null;
    }

    private void EnsureMachinable()
    {
        if (TilingProblems() is [var first, ..])
        {
            throw new InvalidOperationException(first);
        }
    }

    private StockPiece PieceOf(int number)
    {
        var pieces = Pieces;
        return pieces.FirstOrDefault(p => p.Number == number) ?? pieces[0];
    }

    /// <summary>The stock a piece is machined from: the project stock itself when it is not split.</summary>
    private Stock PieceStock(StockPiece piece)
    {
        if (piece.Number == 0)
        {
            return EntityCopy.Clone(session.Stock);
        }

        var stock = piece.ToStock();
        stock.Id = session.Stock.Id;
        return stock;
    }

    /// <summary>Seam planes bounding a piece (min/max X, Y, Z); null where the piece ends at the stock surface.</summary>
    private double?[] ClipPlanes(StockPiece piece)
    {
        if (piece.Number == 0)
        {
            return [];
        }

        const double Tolerance = 1e-9;
        var s = session.Stock;
        return
        [
            piece.MinX > s.OriginX + Tolerance ? piece.MinX : null,
            piece.MaxX < s.OriginX + s.SizeX - Tolerance ? piece.MaxX : null,
            piece.MinY > s.OriginY + Tolerance ? piece.MinY : null,
            piece.MaxY < s.OriginY + s.SizeY - Tolerance ? piece.MaxY : null,
            piece.MinZ > s.OriginZ + Tolerance ? piece.MinZ : null,
            piece.MaxZ < s.OriginZ + s.SizeZ - Tolerance ? piece.MaxZ : null,
        ];
    }

    /// <summary>Hash of the milled models and their transforms.</summary>
    private string PartsKey() => Sha(EntityCopy.Canonical(session.Parts.Where(p => p.Role == BodyRole.Mill)
        .Select(p => new { p.Sha256, p.ImportUnit, p.Transforms, Repaired = p.RepairReport is not null })));

    /// <summary>
    /// The pieces to machine with the part clipped at their seams; pieces that hold none of the part are left out (and
    /// listed in <see cref="EmptyPieces"/>).
    /// </summary>
    private async Task<IReadOnlyList<(StockPiece Piece, Mesh Part)>> PiecePartsAsync(Mesh part, CancellationToken cancellationToken)
    {
        var result = new List<(StockPiece, Mesh)>();
        emptyPieces.Clear();
        var partsKey = PartsKey();
        foreach (var piece in Pieces)
        {
            var clipped = part;
            if (piece.Number != 0)
            {
                var clip = ClipPlanes(piece);
                var key = Sha(EntityCopy.Canonical(new { partsKey, clip }));
                if (!pieceParts.TryGetValue(key, out clipped))
                {
                    clipped = await Task.Run(() => MeshClipper.ClipToBox(part, clip[0], clip[1], clip[2], clip[3], clip[4], clip[5]), cancellationToken).ConfigureAwait(true);
                    if (pieceParts.Count >= MaxPieceParts)
                    {
                        pieceParts.Clear();
                    }

                    pieceParts[key] = clipped;
                }
            }

            if (clipped.TriangleCount == 0)
            {
                emptyPieces.Add(piece.Number);
            }
            else
            {
                result.Add((piece, clipped));
            }
        }

        return result.Count > 0
            ? result
            : throw new InvalidOperationException("No stock piece holds any of the part; place the part on the stock.");
    }

    private Setup SetupCopy(Guid setupId)
    {
        var copy = EntityCopy.Clone(session.Setups.Single(s => s.Id == setupId));
        copy.Operations = [];
        return copy;
    }

    /// <summary>
    /// Depth limit of a 3-axis setup, the tabs and frame it must leave standing (those of every tab setup on the same
    /// axis, created once per piece in <paramref name="layouts"/>) and the notes on its own tabs.
    /// </summary>
    private (double? Depth, Mesh? Tabs, IReadOnlyList<string> Warnings) SideOf(Setup setup, Stock stock, Mesh part, Dictionary<Guid, TabLayout?> layouts)
    {
        if (setup.Orientation != SetupOrientation.ThreeAxis)
        {
            return (null, null, []);
        }

        var meshes = new List<Mesh>();
        var warnings = new List<string>();
        var tabSetups = session.Setups.Where(s =>
            s.Orientation == SetupOrientation.ThreeAxis && s.TabCount > 0 && s.Operations.Any(o => o.Enabled) && StockTurn.SameAxis(s.Face, setup.Face));
        foreach (var other in tabSetups)
        {
            if (!layouts.TryGetValue(other.Id, out var layout))
            {
                layout = StockTurn.DepthLimit(other, session.Setups, stock) is { } depth ? TabSolids.Create(other, depth, stock, part) : null;
                layouts[other.Id] = layout;
            }

            if (layout is not null)
            {
                meshes.Add(layout.Mesh);
                if (other.Id == setup.Id)
                {
                    warnings.AddRange(layout.Warnings);
                }
            }
        }

        return (StockTurn.DepthLimit(setup, session.Setups, stock), meshes.Count == 0 ? null : Mesh.Merge(meshes, "tabs"), warnings);
    }

    /// <summary>What the depth limits and tabs of the 3-axis setups depend on: the faces, holding and active state of every setup.</summary>
    private string SidesKey() => Sha(EntityCopy.Canonical(session.Setups.Select(s => new
    {
        s.Id,
        s.Orientation,
        s.Face,
        s.Spin,
        s.Anchor,
        s.DepthLimit,
        s.TabCount,
        s.TabWidth,
        s.TabThickness,
        s.FrameWidth,
        Active = s.Operations.Any(o => o.Enabled),
    })));

    private string Hash(Operation operation, IReadOnlyList<string> previous, StockPiece piece)
    {
        var tool = session.FindTool(operation.ToolId);
        var input = new
        {
            Generator = GeneratorVersion,
            ToolpathGenerator.Revision,
            Operation = new { operation.Strategy, operation.ToolId, operation.Parameters },
            Setup = SetupCopy(operation.SetupId),
            Tool = tool,
            Collet = tool is null ? null : session.ColletFor(tool),
            Stock = piece.Number == 0
                ? (object)session.Stock
                : new { piece.MinX, piece.MinY, piece.MinZ, piece.SizeX, piece.SizeY, piece.SizeZ, Clip = ClipPlanes(piece) },
            Parts = PartsKey(),
            Sides = SidesKey(),
            session.KeepOuts,
            session.Project.Tolerances,
            session.Machine,
            Previous = previous,
        };
        return Sha(EntityCopy.Canonical(input));
    }

    /// <summary>Identifies the inputs of a simulation: toolpaths and conditions of every step of every piece.</summary>
    private string Fingerprint()
    {
        var order = ExecutionOrder();
        var steps = new List<object>();
        foreach (var piece in Pieces)
        {
            var hashes = new List<string>();
            foreach (var operation in order)
            {
                var hash = Hash(operation, hashes, piece);
                hashes.Add(hash);
                steps.Add(new { piece.Number, hash, operation.Conditions, operation.Name });
            }
        }

        return Sha(EntityCopy.Canonical(steps));
    }

    private async Task<IReadOnlyList<(StockPiece Piece, SimulationJob Job)>> CreateJobsAsync(IProgress<CamProgress>? progress, CancellationToken cancellationToken)
    {
        var order = ExecutionOrder();
        var part = await session.MillPartAsync(cancellationToken).ConfigureAwait(true);
        if (part.TriangleCount == 0)
        {
            throw new InvalidOperationException("Import a part to mill first.");
        }

        var pieces = await PiecePartsAsync(part, cancellationToken).ConfigureAwait(true);
        if (pieces.Any(p => order.Any(o => GetToolpath(o.Id, p.Piece.Number) is null)))
        {
            await GenerateAsync(progress, cancellationToken).ConfigureAwait(true);
        }

        var jobs = new List<(StockPiece, SimulationJob)>();
        foreach (var (piece, piecePart) in pieces)
        {
            var steps = new List<SimulationStep>();
            foreach (var operation in order)
            {
                var tool = session.FindTool(operation.ToolId)!;
                steps.Add(new MillingStep
                {
                    Id = operation.Id,
                    Name = operation.Name,
                    SetupId = operation.SetupId,
                    Toolpath = GetToolpath(operation.Id, piece.Number)!.Toolpath,
                    Tool = tool,
                    Collet = session.ColletFor(tool),
                    Conditions = operation.Conditions is null ? null : EntityCopy.Clone(operation.Conditions),
                    Limiter = operation.Conditions is not null && limiters.TryGetValue(operation.Id, out var limiter) ? limiter : null,
                });
            }

            jobs.Add((piece, new SimulationJob
            {
                Compute = null!,
                Part = piecePart,
                Stock = PieceStock(piece),
                StockMesh = piece.Number == 0 ? session.LoadStockMesh() : null,
                Machine = session.Machine,
                Tolerances = EntityCopy.Clone(session.Project.Tolerances),
                KeepOuts = [.. session.KeepOuts.Select(EntityCopy.Clone)],
                Steps = steps,
            }));
        }

        return jobs;
    }

    private async Task<GeneratedToolpath?> LoadCachedAsync(Operation operation, int piece, string hash, CancellationToken cancellationToken)
    {
        if (toolpaths.TryGetValue((operation.Id, piece), out var cached) && cached.InputHash == hash)
        {
            return cached with { Operation = operation, FromCache = true };
        }

        var row = await session.GetToolpathResultAsync(operation.Id, piece, cancellationToken).ConfigureAwait(true);
        if (row is null || row.InputHash != hash)
        {
            return null;
        }

        try
        {
            var path = PartGeometry.Resolve(session.Folder, row.CachePath);
            var toolpath = await Task.Run(() => ToolpathFile.Load(path), cancellationToken).ConfigureAwait(true);
            return toolpath.OperationId == operation.Id ? new GeneratedToolpath(operation, hash, toolpath, true, piece) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task StoreAsync(GeneratedToolpath generated, CancellationToken cancellationToken)
    {
        var path = session.ToolpathCachePath(generated.Operation.Id, generated.Piece);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Task.Run(() => ToolpathFile.Save(generated.Toolpath, path), cancellationToken).ConfigureAwait(true);
        var toolpath = generated.Toolpath;
        await session.SaveToolpathResultAsync(new ToolpathResult
        {
            OperationId = generated.Operation.Id,
            Piece = generated.Piece,
            InputHash = generated.InputHash,
            CachePath = Path.GetRelativePath(session.Folder, path).Replace('\\', '/'),
            ComputedAt = time.GetUtcNow(),
            MoveCount = toolpath.MoveCount,
            CutLength = toolpath.CutLength,
            RapidLength = toolpath.RapidLength,
            EstimatedSeconds = EstimateSeconds(toolpath, generated.Operation.Conditions),
        }, cancellationToken).ConfigureAwait(true);
    }

    private double EstimateSeconds(Toolpath toolpath, CuttingConditions? conditions)
    {
        if (conditions is not { FeedRate: > 0 })
        {
            return 0;
        }

        var rapid = new[] { Core.Machines.AxisName.X, Core.Machines.AxisName.Y, Core.Machines.AxisName.Z }
            .Select(a => session.Machine.GetAxis(a)?.MaxRatePerMinute ?? 0)
            .Where(r => r > 0)
            .DefaultIfEmpty(5000)
            .Min();
        var plunge = conditions.PlungeRate > 0 ? conditions.PlungeRate : conditions.FeedRate;
        double minutes = 0;
        for (var i = 1; i < toolpath.Moves.Count; i++)
        {
            var rate = toolpath.Moves[i].Kind switch
            {
                MoveKind.Rapid => rapid,
                MoveKind.Plunge => plunge,
                _ => conditions.FeedRate,
            };
            minutes += Toolpath.MoveLength(toolpath.Moves[i - 1], toolpath.Moves[i]) / rate;
        }

        return minutes * 60;
    }

    private async Task<IReadOnlyList<Core.Learning.FeedCorrection>> LoadCorrectionsAsync(Material material, CancellationToken cancellationToken)
    {
        await using var library = await LibraryDatabase.OpenAsync(session.LibraryPath, cancellationToken).ConfigureAwait(true);
        return await library.FeedCorrections.AsNoTracking().Where(c => c.MaterialId == material.Id).ToListAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ReplaceOutputAsync(IReadOnlyList<PostOutput> outputs, CancellationToken cancellationToken)
    {
        var db = session.Database;
        var now = time.GetUtcNow();
        var keep = outputs.Select(o => o.FileName.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var stale = await db.GcodeFiles.Where(g => !keep.Contains(g.Path)).ToListAsync(cancellationToken).ConfigureAwait(true);
        foreach (var file in stale)
        {
            try
            {
                File.Delete(PartGeometry.Resolve(session.Folder, file.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A file that cannot be deleted is left behind; its row is removed so it is no longer offered.
            }
        }

        db.GcodeFiles.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        foreach (var output in outputs)
        {
            await GcodeFileStore.SaveAsync(db, session.Folder, output, now, cancellationToken).ConfigureAwait(true);
        }

        foreach (var entry in db.ChangeTracker.Entries<GcodeFile>().ToList())
        {
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }
}
