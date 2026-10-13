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

/// <summary>Toolpath of an operation and the inputs it was generated from.</summary>
public sealed record GeneratedToolpath(Operation Operation, string InputHash, Toolpath Toolpath, bool FromCache);

/// <summary>Feeds of one file.</summary>
public sealed record CamFeedPlan(CamGroup Group, ToolGroupPlan Plan);

/// <summary>
/// Generate → feeds → simulate → post for an open project. Called from the UI thread (it reads and writes the
/// session); the heavy work runs on the thread pool with detached copies. Toolpaths are cached in
/// <c>toolpaths/</c> keyed by a hash of every input, so only operations whose inputs changed are regenerated.
/// </summary>
public sealed class CamPipeline : IDisposable
{
    /// <summary>Bumped whenever generated output for identical inputs changes.</summary>
    public static readonly string GeneratorVersion = typeof(ToolpathGenerator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly ProjectSession session;
    private readonly ComputeService compute;
    private readonly TimeProvider time;
    private readonly Dictionary<Guid, GeneratedToolpath> toolpaths = [];
    private readonly Dictionary<Guid, EngagementLimiter> limiters = [];
    private readonly List<CamFeedPlan> feedPlans = [];
    private SimulationResult? simulation;
    private SimulationJob? simulationJob;
    private string? simulationFingerprint;

    public CamPipeline(ProjectSession session, ComputeService compute, TimeProvider? time = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.compute = compute ?? throw new ArgumentNullException(nameof(compute));
        this.time = time ?? TimeProvider.System;
        compute.Resetting += OnComputeResetting;
    }

    public IReadOnlyList<CamFeedPlan> FeedPlans => feedPlans;

    /// <summary>Last simulation (stock and verification report), null before the first run.</summary>
    public SimulationResult? Simulation => simulation;

    /// <summary>True when the project changed since <see cref="Simulation"/> was computed.</summary>
    public bool SimulationIsStale => simulation is null || simulationFingerprint != Fingerprint();

    public GeneratedToolpath? GetToolpath(Guid operationId) =>
        toolpaths.TryGetValue(operationId, out var generated) && generated.InputHash == InputHash(generated.Operation) ? generated : null;

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

    /// <summary>Up-to-date toolpaths in machining order (operations without one are skipped).</summary>
    public IReadOnlyList<GeneratedToolpath> CurrentToolpaths() => [.. ExecutionOrder().Select(o => GetToolpath(o.Id)).OfType<GeneratedToolpath>()];

    /// <summary>Hash of everything a toolpath depends on, including the operations machined before it.</summary>
    public string InputHash(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var order = ExecutionOrder();
        var hashes = new Dictionary<Guid, string>();
        foreach (var op in order)
        {
            hashes[op.Id] = Hash(op, [.. hashes.Values]);
            if (op.Id == operation.Id)
            {
                return hashes[op.Id];
            }
        }

        return Hash(operation, [.. hashes.Values]);
    }

    // ----- Generate -----

    /// <summary>Generates (or loads from the cache) the toolpath of every enabled operation.</summary>
    public async Task<IReadOnlyList<GeneratedToolpath>> GenerateAsync(IProgress<CamProgress>? progress = null, CancellationToken cancellationToken = default)
    {
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

        var stockMesh = session.LoadStockMesh();
        var stock = EntityCopy.Clone(session.Stock);
        var keepOuts = session.KeepOuts.Select(EntityCopy.Clone).ToList();
        var tolerances = EntityCopy.Clone(session.Project.Tolerances);
        var machine = session.Machine;
        var results = new List<GeneratedToolpath>();
        var previous = new List<PreviousCut>();
        var hashes = new List<string>();
        using var lease = await compute.AcquireAsync(cancellationToken).ConfigureAwait(true);
        for (var i = 0; i < order.Count; i++)
        {
            var operation = order[i];
            var tool = session.FindTool(operation.ToolId)!;
            var hash = Hash(operation, hashes);
            hashes.Add(hash);
            var item = operation.Name;
            progress?.Report(new CamProgress("Toolpaths", item, (double)i / order.Count));
            var generated = await LoadCachedAsync(operation, hash, cancellationToken).ConfigureAwait(true);
            if (generated is null)
            {
                var request = new ToolpathRequest
                {
                    Compute = lease.Context,
                    Operation = EntityCopy.Clone(operation),
                    Setup = SetupCopy(operation.SetupId),
                    Tool = tool,
                    Collet = session.ColletFor(tool),
                    Stock = stock,
                    StockMesh = stockMesh,
                    Part = part,
                    KeepOuts = keepOuts,
                    Tolerances = tolerances,
                    Machine = machine,
                    PreviousCuts = [.. previous],
                };
                var (start, count) = (i, order.Count);
                var inner = progress is null ? null : new Progress<double>(f => progress.Report(new CamProgress("Toolpaths", item, (start + f) / count)));
                var toolpath = await Task.Run(() => ToolpathGenerator.Generate(request, inner, cancellationToken), cancellationToken).ConfigureAwait(true);
                generated = new GeneratedToolpath(operation, hash, toolpath, false);
                await StoreAsync(generated, cancellationToken).ConfigureAwait(true);
            }

            toolpaths[operation.Id] = generated;
            results.Add(generated);
            previous.Add(new PreviousCut(generated.Toolpath, tool));
        }

        progress?.Report(new CamProgress("Toolpaths", string.Empty, 1));
        return results;
    }

    /// <summary>Loads the cached toolpaths whose inputs are unchanged, without generating any; returns how many are current.</summary>
    public async Task<int> LoadCacheAsync(CancellationToken cancellationToken = default)
    {
        var hashes = new List<string>();
        var current = 0;
        foreach (var operation in ExecutionOrder())
        {
            var hash = Hash(operation, hashes);
            hashes.Add(hash);
            if (await LoadCachedAsync(operation, hash, cancellationToken).ConfigureAwait(true) is { } generated)
            {
                toolpaths[operation.Id] = generated;
                current++;
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
                    CutLength = GetToolpath(operation.Id)?.Toolpath.CutLength ?? 0,
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

    /// <summary>Simulates every enabled operation on the dexel stock and verifies the result.</summary>
    public async Task<SimulationResult> SimulateAsync(IProgress<CamProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var job = await CreateJobAsync(progress, cancellationToken).ConfigureAwait(true);
        var fingerprint = Fingerprint();
        using var lease = await compute.AcquireAsync(cancellationToken).ConfigureAwait(true);
        job = WithCompute(job, lease.Context);
        var inner = progress is null ? null : new Progress<double>(f => progress.Report(new CamProgress("Simulation", string.Empty, f)));
        var result = await Task.Run(() => Simulator.Run(job, inner, cancellationToken), cancellationToken).ConfigureAwait(true);
        ResetSimulation();
        simulation = result;
        simulationJob = job;
        simulationFingerprint = fingerprint;
        return result;
    }

    /// <summary>Step-by-step playback of the last simulation (seek with <see cref="SeekAsync"/>).</summary>
    public async Task<SimulationSession> CreatePlaybackAsync(CancellationToken cancellationToken = default)
    {
        var job = simulationJob ?? throw new InvalidOperationException("Run the simulation first.");
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
    /// Posts one G-code file per <see cref="CamGroup"/> and replaces the project's previous output. Runs the
    /// simulation first when it is missing or stale; errors in the verification block output unless overridden.
    /// </summary>
    public async Task<IReadOnlyList<PostOutput>> PostAsync(bool overrideVerification = false, IProgress<CamProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (SimulationIsStale)
        {
            await SimulateAsync(progress, cancellationToken).ConfigureAwait(true);
        }

        var report = simulation!.Report;
        if (!report.MayWriteOutput(overrideVerification))
        {
            throw new PostException($"The simulation found {report.Issues.Count(i => i.Severity == IssueSeverity.Error)} error(s); review the verification report or override it.");
        }

        var stockBounds = (session.StockSolid() ?? throw new InvalidOperationException("The stock mesh is missing.")).Bounds;
        double? stockRadius = session.Stock.Type == StockType.Cylinder ? session.Stock.Diameter / 2 : null;
        var post = new FluidNcMillingPost();
        var outputs = new List<PostOutput>();
        var groups = Plan();
        foreach (var group in groups)
        {
            progress?.Report(new CamProgress("Post", group.Tool.Name, (double)outputs.Count / groups.Count));
            var operations = new List<PostOperation>();
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var operation in group.Operations)
            {
                var generated = GetToolpath(operation.Id) ?? throw new InvalidOperationException($"Generate the toolpath of '{operation.Name}' first.");
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
                TouchPlate = TouchPlateSpot.FromStockBounds(operations[0].Toolpath.Frame, stockBounds, stockRadius),
                Verification = report,
                OverrideVerification = overrideVerification,
                InputHashes = hashes,
                GeneratorVersion = GeneratorVersion,
            };
            outputs.Add(await Task.Run(() => post.Post(request), cancellationToken).ConfigureAwait(true));
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
        simulation?.Dispose();
        simulation = null;
        simulationJob = null;
        simulationFingerprint = null;
    }

    private Setup SetupCopy(Guid setupId)
    {
        var copy = EntityCopy.Clone(session.Setups.Single(s => s.Id == setupId));
        copy.Operations = [];
        return copy;
    }

    private string Hash(Operation operation, IReadOnlyList<string> previous)
    {
        var tool = session.FindTool(operation.ToolId);
        var input = new
        {
            Generator = GeneratorVersion,
            Operation = new { operation.Strategy, operation.ToolId, operation.Parameters },
            Setup = SetupCopy(operation.SetupId),
            Tool = tool,
            Collet = tool is null ? null : session.ColletFor(tool),
            session.Stock,
            Parts = session.Parts.Where(p => p.Role == BodyRole.Mill)
                .Select(p => new { p.Sha256, p.ImportUnit, p.Transforms, Repaired = p.RepairReport is not null }),
            session.KeepOuts,
            session.Project.Tolerances,
            session.Machine,
            Previous = previous,
        };
        return Sha(EntityCopy.Canonical(input));
    }

    /// <summary>Identifies the inputs of a simulation: toolpaths and conditions of every step.</summary>
    private string Fingerprint()
    {
        var hashes = new List<string>();
        var steps = new List<object>();
        foreach (var operation in ExecutionOrder())
        {
            var hash = Hash(operation, hashes);
            hashes.Add(hash);
            steps.Add(new { hash, operation.Conditions, operation.Name });
        }

        return Sha(EntityCopy.Canonical(steps));
    }

    private async Task<SimulationJob> CreateJobAsync(IProgress<CamProgress>? progress, CancellationToken cancellationToken)
    {
        var order = ExecutionOrder();
        if (order.Any(o => GetToolpath(o.Id) is null))
        {
            await GenerateAsync(progress, cancellationToken).ConfigureAwait(true);
        }

        var steps = new List<SimulationStep>();
        foreach (var operation in order)
        {
            var tool = session.FindTool(operation.ToolId)!;
            steps.Add(new MillingStep
            {
                Id = operation.Id,
                Name = operation.Name,
                SetupId = operation.SetupId,
                Toolpath = GetToolpath(operation.Id)!.Toolpath,
                Tool = tool,
                Collet = session.ColletFor(tool),
                Conditions = operation.Conditions is null ? null : EntityCopy.Clone(operation.Conditions),
                Limiter = operation.Conditions is not null && limiters.TryGetValue(operation.Id, out var limiter) ? limiter : null,
            });
        }

        return new SimulationJob
        {
            Compute = null!,
            Part = await session.MillPartAsync(cancellationToken).ConfigureAwait(true),
            Stock = EntityCopy.Clone(session.Stock),
            StockMesh = session.LoadStockMesh(),
            Machine = session.Machine,
            Tolerances = EntityCopy.Clone(session.Project.Tolerances),
            KeepOuts = [.. session.KeepOuts.Select(EntityCopy.Clone)],
            Steps = steps,
        };
    }

    private async Task<GeneratedToolpath?> LoadCachedAsync(Operation operation, string hash, CancellationToken cancellationToken)
    {
        if (toolpaths.TryGetValue(operation.Id, out var cached) && cached.InputHash == hash)
        {
            return cached with { Operation = operation, FromCache = true };
        }

        var row = await session.GetToolpathResultAsync(operation.Id, cancellationToken).ConfigureAwait(true);
        if (row is null || row.InputHash != hash)
        {
            return null;
        }

        try
        {
            var path = PartGeometry.Resolve(session.Folder, row.CachePath);
            var toolpath = await Task.Run(() => ToolpathFile.Load(path), cancellationToken).ConfigureAwait(true);
            return toolpath.OperationId == operation.Id ? new GeneratedToolpath(operation, hash, toolpath, true) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task StoreAsync(GeneratedToolpath generated, CancellationToken cancellationToken)
    {
        var path = session.ToolpathCachePath(generated.Operation.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Task.Run(() => ToolpathFile.Save(generated.Toolpath, path), cancellationToken).ConfigureAwait(true);
        var toolpath = generated.Toolpath;
        await session.SaveToolpathResultAsync(new ToolpathResult
        {
            OperationId = generated.Operation.Id,
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
