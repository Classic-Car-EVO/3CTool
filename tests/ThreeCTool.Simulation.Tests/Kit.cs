using System.Numerics;

using ThreeCTool.Compute;
using ThreeCTool.Core;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Geometry;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Simulation.Tests;

public sealed class ComputeFixture : IDisposable
{
    public ComputeContext Compute { get; } = ComputeContext.Create(ComputeBackend.Cpu);

    public void Dispose() => Compute.Dispose();
}

[CollectionDefinition(Name)]
public sealed class ComputeGroup : ICollectionFixture<ComputeFixture>
{
    public const string Name = "Compute";
}

internal static class Kit
{
    public static readonly Guid SetupId = Guid.NewGuid();

    /// <summary>6 mm flat end mill: 18 mm flutes, 36 mm stickout.</summary>
    public static Tool Flat(double diameter = 6, double stickout = 36) =>
        new() { Name = $"flat {diameter}", Type = ToolType.FlatEnd, Diameter = diameter, FluteLength = 3 * diameter, ShankDiameter = diameter, Stickout = stickout };

    /// <summary>Collet nut Ø20 × 25 mm below the spindle face.</summary>
    public static Collet Collet() => new() { Name = "nut", ShankDiameter = 6, Holder = [new HolderSegment { Diameter = 20, Length = 25 }] };

    public static MachineProfile Machine(double travelX = 1300) => new()
    {
        Name = "test mill",
        Axes =
        [
            new AxisConfig { Axis = AxisName.X, MaxRatePerMinute = 1500, AccelerationPerSecond2 = 25, MaxTravel = travelX },
            new AxisConfig { Axis = AxisName.Y, MaxRatePerMinute = 1500, AccelerationPerSecond2 = 25, MaxTravel = 750 },
            new AxisConfig { Axis = AxisName.Z, MaxRatePerMinute = 1000, AccelerationPerSecond2 = 25, MaxTravel = 600 },
            new AxisConfig { Axis = AxisName.A, MaxRatePerMinute = 1000, AccelerationPerSecond2 = 25 },
        ],
        Toolheads =
        [
            new Toolhead
            {
                Name = "spindle",
                Type = ToolheadType.Spindle,
                CollisionModel = [new SimpleSolid { Name = "spindle body", Shape = SolidShape.Cylinder, BottomZ = 25, Height = 50, Radius = 37.5 }],
            },
        ],
    };

    public static Stock BoxStock(Vector3 min, Vector3 max) => new()
    {
        Type = StockType.Box,
        OriginX = min.X,
        OriginY = min.Y,
        OriginZ = min.Z,
        SizeX = max.X - min.X,
        SizeY = max.Y - min.Y,
        SizeZ = max.Z - min.Z,
    };

    public static Mesh Box(Vector3 min, Vector3 max) => TestMeshes.Box(min, max);

    public static MillingStep Milling(string name, Tool tool, IEnumerable<ToolpathMove> moves, SetupFrame? frame = null, Collet? collet = null) => new()
    {
        Name = name,
        SetupId = SetupId,
        Tool = tool,
        Collet = collet,
        Conditions = new CuttingConditions { FeedRate = 600, PlungeRate = 200 },
        Toolpath = new Toolpath { Frame = frame ?? SetupFrame.Identity, Moves = [.. moves] },
    };

    public static ToolpathMove Rapid(double x, double y, double z, double a = 0) => new(MoveKind.Rapid, x, y, z, a);

    public static ToolpathMove Cut(double x, double y, double z, double a = 0) => new(MoveKind.Cut, x, y, z, a);

    public static ToolpathMove Plunge(double x, double y, double z) => new(MoveKind.Plunge, x, y, z);

    /// <summary>Plunge at (x0, y), cut to (x1, y) at depth z and retract to z = 40.</summary>
    public static ToolpathMove[] Slot(double x0, double x1, double y, double z) =>
        [Rapid(x0, y, 40), Plunge(x0, y, z), Cut(x1, y, z), Rapid(x1, y, 40)];

    public static SimulationJob Job(ComputeContext compute, Mesh part, Stock stock, IReadOnlyList<SimulationStep> steps, Action<JobOptions>? configure = null)
    {
        var o = new JobOptions();
        configure?.Invoke(o);
        return new SimulationJob
        {
            Compute = compute,
            Part = part,
            Stock = stock,
            Machine = o.Machine ?? Machine(),
            KeepOuts = o.KeepOuts,
            WorkOffsets = o.WorkOffsets,
            Steps = steps,
            Options = new SimulationOptions { Resolution = o.Resolution },
        };
    }

    internal sealed class JobOptions
    {
        public float Resolution { get; set; } = 0.5f;

        public MachineProfile? Machine { get; set; }

        public List<KeepOutZone> KeepOuts { get; } = [];

        public Dictionary<Guid, Vector3> WorkOffsets { get; } = [];
    }

    public static IEnumerable<VerificationIssue> Errors(this VerificationReport report) =>
        report.Issues.Where(i => i.Severity == IssueSeverity.Error);

    public static string Describe(this VerificationReport report) =>
        string.Join(Environment.NewLine, report.Issues.Select(i => $"{i.Severity} {i.Kind} [{i.FirstMove}-{i.LastMove}] {i.Body}: {i.Message}"));
}
