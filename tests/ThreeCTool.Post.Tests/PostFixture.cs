using System.Numerics;

using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Materials;
using ThreeCTool.Core.Projects;
using ThreeCTool.Core.Tooling;
using ThreeCTool.Simulation;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Post.Tests;

/// <summary>Machine, setup and request builders shared by the post tests.</summary>
internal static class PostFixture
{
    /// <summary>Same axes as the FluidNC simulator: X 0..1300, Y 0..750, Z −650..0 and an A axis.</summary>
    public static MachineProfile Machine() => new()
    {
        Name = "Test mill",
        Axes =
        [
            Linear(AxisName.X, 1300, cycle: 2, positive: false),
            Linear(AxisName.Y, 750, cycle: 2, positive: false),
            Linear(AxisName.Z, 650, cycle: 1, positive: true),
            new AxisConfig { Axis = AxisName.A, StepsPerUnit = 44.444, MaxRatePerMinute = 7200, AccelerationPerSecond2 = 200 },
        ],
        Spindle = new SpindleConfig { MinRpm = 1500, MaxRpm = 30000 },
        Probe = new ProbeConfig { ToolSetterX = 50, ToolSetterY = 50 },
        SafeZMachine = -5,
    };

    public static Tool Tool(string name = "Flat 6") => new()
    {
        Name = name,
        Type = ToolType.FlatEnd,
        Diameter = 6,
        FluteCount = 2,
        Stickout = 20,
    };

    public static Setup Setup(
        SetupOrientation orientation = SetupOrientation.ThreeAxis,
        ProbeMethod method = ProbeMethod.TouchPlate,
        WorkOffset workOffset = WorkOffset.G54,
        double indexAngle = 0) => new()
        {
            Order = 1,
            Name = "Top",
            Orientation = orientation,
            IndexAngle = indexAngle,
            ProbeMethod = method,
            WorkOffset = workOffset,
        };

    public static StepSummary Summary(double duration = 0, float[]? factors = null) => new()
    {
        StepId = Guid.NewGuid(),
        Name = "Contour",
        Duration = duration,
        MoveEndTimes = [],
        FeedFactors = factors ?? [],
    };

    public static MillingPostRequest Request(
        IReadOnlyList<ToolpathMove> moves,
        Setup? setup = null,
        Tool? tool = null,
        MachineProfile? machine = null,
        bool first = true,
        double rpm = 18000,
        bool verified = true,
        VerificationReport? verification = null,
        bool overrideVerification = false,
        StepSummary? summary = null,
        Vector3? workOffset = null,
        TouchPlateSpot? touchPlate = null,
        Project? project = null,
        StockPiece? piece = null,
        int pieceCount = 0)
    {
        setup ??= Setup();
        tool ??= Tool();
        var operation = new Operation { Name = "Contour", SetupId = setup.Id, ToolId = tool.Id };
        var toolpath = new Toolpath
        {
            OperationId = operation.Id,
            ToolId = tool.Id,
            Frame = new SetupFrame(setup.Orientation, 0, 0, 0, setup.IndexAngle, setup.Face, setup.Spin ?? 0),
            Clearance = 5,
            Moves = [.. moves],
        };
        var conditions = new CuttingConditions { Rpm = rpm, FeedRate = 1000, PlungeRate = 300 };
        return new MillingPostRequest
        {
            Project = project ?? new Project { Name = "Demo" },
            Setup = setup,
            Machine = machine ?? Machine(),
            Tool = tool,
            Collet = new Collet { Name = "ER11 6", ShankDiameter = 6 },
            Material = new Material { Name = "Aluminium" },
            Rpm = rpm,
            Operations = [new PostOperation(operation, toolpath, conditions, summary)],
            FirstInSetup = first,
            WorkOffset = workOffset,
            TouchPlate = touchPlate ?? new TouchPlateSpot(20, 15, 0, setup.Orientation == SetupOrientation.ThreeAxis ? -20 : null),
            Verification = verification ?? (verified ? new VerificationReport() : null),
            OverrideVerification = overrideVerification,
            InputHashes = new Dictionary<string, string> { ["toolpaths"] = "abc" },
            GeneratorVersion = "9.9.9",
            Piece = piece,
            PieceCount = pieceCount,
        };
    }

    /// <summary>Rapid to (0, 0, 5), plunge to Z−1, cut to (10, 0) and (10, 5), retract.</summary>
    public static List<ToolpathMove> SimpleMoves() =>
    [
        new(MoveKind.Rapid, 0, 0, 5),
        new(MoveKind.Plunge, 0, 0, -1),
        new(MoveKind.Cut, 10, 0, -1),
        new(MoveKind.Cut, 10, 5, -1),
        new(MoveKind.Rapid, 10, 5, 5),
    ];

    private static AxisConfig Linear(AxisName axis, double travel, int cycle, bool positive) => new()
    {
        Axis = axis,
        StepsPerUnit = 100,
        MaxRatePerMinute = 6000,
        AccelerationPerSecond2 = 300,
        MaxTravel = travel,
        SoftLimits = true,
        HomingCycle = cycle,
        HomingPositiveDirection = positive,
    };
}
