using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;
using ThreeCTool.Simulation;
using ThreeCTool.Toolpaths;

namespace ThreeCTool.Post.Tests;

public sealed class FluidNcMillingPostTests
{
    private readonly FluidNcMillingPost post = new();

    [Fact]
    public void Post_TouchPlateFile_MatchesGoldenOutput()
    {
        var output = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), summary: PostFixture.Summary(125)));

        string[] expected =
        [
            "(3C Tool milling program)",
            "(Project: Demo)",
            "(Setup 1: Top, G54, 3-axis)",
            "(Tool: Flat 6, FlatEnd, D6 mm, 2 flutes, stickout 20 mm)",
            "(Collet: ER11 6)",
            "(Material: Aluminium)",
            "(Spindle: 18000 RPM)",
            "(Probe: touch plate on the material at X20 Y15, or on the bed at X0 Y0 if chosen at the machine)",
            "(Operations: Contour)",
            "(Estimated time: 00:02:05)",
            "(Generator: 3C Tool 9.9.9)",
            "(Input toolpaths: abc)",
            "(MC:TOOL STICKOUT 20)",
            "G21 G90 G94 G17 G91.1 G54",
            "G53 G0 Z-5",
            "M5 M9",
            "(Insert tool Flat 6 in collet ER11 6, stickout 20 mm, set the spindle dial to 18000 RPM)",
            "M0",
            "(MC:PROBE BEGIN TOUCHPLATE)",
            "G49",
            "G53 G0 Z-5",
            "(Probe the touch plate on the bed at X0 Y0? Yes: plate on the bed at X0 Y0. No: plate on the material at X20 Y15)",
            "(MC:ASK BEGIN)",
            "G0 X0 Y0",
            "(Place the touch plate on the bed at X0 Y0 under the tool, at most 80 mm below it, and attach the probe clip)",
            "M0",
            "G91 G38.2 Z-80 F200",
            "G0 Z2",
            "G38.2 Z-4 F25",
            "G90",
            "G10 L20 P1 Z-10",
            "(MC:ASK ELSE)",
            "G0 X20 Y15",
            "(Place the touch plate on the material at X20 Y15 under the tool, at most 80 mm below it, and attach the probe clip)",
            "M0",
            "G91 G38.2 Z-80 F200",
            "G0 Z2",
            "G38.2 Z-4 F25",
            "G90",
            "G10 L20 P1 Z10",
            "(MC:ASK END)",
            "G53 G0 Z-5",
            "(MC:PROBE END)",
            "(Remove the touch plate and the probe clip)",
            "M0",
            "M3 S18000",
            "(Start the spindle: confirm it runs at 18000 RPM)",
            "M0",
            "(Operation: Contour, AdaptiveRoughing)",
            "G53 G0 Z-5",
            "G0 X0 Y0",
            "Z5",
            "G1 Z-1 F300",
            "X10 F1000",
            "Y5",
            "G0 Z5",
            "G53 G0 Z-5",
            "M9",
            "M5",
            "M30",
        ];
        Assert.Equal(expected, output.Lines);
        Assert.Equal(string.Join('\n', expected) + "\n", output.Text);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output.Text))), output.Sha256);
        Assert.Equal(expected.Length, output.LineCount);
        Assert.Equal(125, output.EstimatedSeconds);
        Assert.Equal(18000, output.Rpm);
        Assert.Equal("gcode/01_Top_T01_Flat_6.nc", output.FileName);
    }

    [Fact]
    public void Post_SameRequest_IsDeterministic()
    {
        var setup = PostFixture.Setup();
        var tool = PostFixture.Tool();
        var a = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), setup, tool));
        var b = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), setup, tool));
        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.Sha256, b.Sha256);
    }

    [Fact]
    public void Post_ToolSetterSetup_ProbesOnlyTheTouchPlate()
    {
        var setup = PostFixture.Setup(method: ProbeMethod.ToolSetter, workOffset: WorkOffset.G59);
        var expected = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), PostFixture.Setup(workOffset: WorkOffset.G59))).Lines.Skip(1);

        foreach (var first in new[] { true, false })
        {
            var lines = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), setup, first: first)).Lines;
            Assert.Single(lines, "(MC:PROBE BEGIN TOUCHPLATE)");
            Assert.DoesNotContain(lines, l => l.Contains("G43.1", StringComparison.Ordinal) || l.Contains('#') || l.Contains("X50 Y50", StringComparison.Ordinal));
            Assert.Equal(2, lines.Count(l => l.StartsWith("G91 G38.2", StringComparison.Ordinal)));
        }

        Assert.Equal(expected, post.Post(PostFixture.Request(PostFixture.SimpleMoves(), setup)).Lines.Skip(1));
    }

    [Fact]
    public void Post_TouchPlateLaterFile_ProbesThePlateAgain()
    {
        var lines = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), first: false)).Lines;

        Assert.Single(lines, "(MC:PROBE BEGIN TOUCHPLATE)");
        Assert.Contains("G10 L20 P1 Z10", lines);
        Assert.Contains("G10 L20 P1 Z-10", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("G43.1", StringComparison.Ordinal));
    }

    [Fact]
    public void Post_WorkOffsetAndPlate_UseTheSetupOffsetAndPlateThickness()
    {
        var setup = PostFixture.Setup(workOffset: WorkOffset.G56);
        var machine = PostFixture.Machine();
        machine.Probe.TouchPlateThickness = 12.5;
        var lines = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), setup, machine: machine,
            touchPlate: new TouchPlateSpot(1, 2, -3))).Lines;

        Assert.Contains("G21 G90 G94 G17 G91.1 G56", lines);
        Assert.Contains("G0 X1 Y2", lines);
        Assert.Contains("G10 L20 P3 Z9.5", lines);
        Assert.DoesNotContain("(MC:ASK BEGIN)", lines);

        var bed = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), setup, machine: machine,
            touchPlate: new TouchPlateSpot(1, 2, -3, -25))).Lines;
        Assert.Contains("G10 L20 P3 Z-12.5", bed);
        Assert.Contains("G10 L20 P3 Z9.5", bed);
    }

    [Fact]
    public void Post_SpindleCoolantAndDustExtraction_AreSwitchedOnAndOff()
    {
        var machine = PostFixture.Machine();
        machine.Coolant = new CoolantConfig { Mode = CoolantMode.Flood, DustExtractionOnCode = "M62 P1", DustExtractionOffCode = "M63 P1" };
        var lines = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), machine: machine, rpm: 12000)).Lines.ToList();

        var spindle = lines.IndexOf("M3 S12000");
        Assert.Equal(["M3 S12000", "(Start the spindle: confirm it runs at 12000 RPM)", "M0", "M8", "M62 P1"], lines.Skip(spindle).Take(5));
        Assert.Equal(["G53 G0 Z-5", "M9", "M63 P1", "M5", "M30"], lines.TakeLast(5));
    }

    [Fact]
    public void Post_IndexedSetup_RotatesBeforeTheFirstMoveAndThePlate()
    {
        var setup = PostFixture.Setup(SetupOrientation.Indexed, indexAngle: 90);
        List<ToolpathMove> moves =
        [
            new(MoveKind.Rapid, 0, 0, 30, 90),
            new(MoveKind.Plunge, 0, 0, 20, 90),
            new(MoveKind.Cut, 10, 0, 20, 90),
            new(MoveKind.Rapid, 10, 0, 30, 90),
        ];
        var lines = post.Post(PostFixture.Request(moves, setup)).Lines.ToList();

        var plate = lines.IndexOf("(MC:PROBE BEGIN TOUCHPLATE)");
        Assert.Equal(["G49", "G53 G0 Z-5", "G0 A90", "X20 Y15"], lines.Skip(plate + 1).Take(4));
        Assert.DoesNotContain("(MC:ASK BEGIN)", lines);
        var operation = lines.FindIndex(l => l.StartsWith("(Operation:", StringComparison.Ordinal));
        Assert.Equal(["G53 G0 Z-5", "G0 X0 Y0 A90", "Z30", "G1 Z20 F300", "X10 F1000", "G0 Z30"], lines.Skip(operation + 1).Take(6));
        Assert.Contains("(Setup 1: Top, G54, indexed A90)", lines);
    }

    [Fact]
    public void Post_RotaryFeedMoves_UseInverseTimeWithAFeedOnEveryMove()
    {
        var setup = PostFixture.Setup(SetupOrientation.SimultaneousRotary);
        List<ToolpathMove> moves =
        [
            new(MoveKind.Rapid, 0, 0, 30, 0),
            new(MoveKind.Plunge, 0, 0, 20, 0),
            new(MoveKind.Cut, 0, 0, 20, 90),
            new(MoveKind.Cut, 1, 0, 20, 180),
            new(MoveKind.Cut, 10, 0, 20, 180),
            new(MoveKind.Rapid, 10, 0, 30, 180),
        ];
        var lines = post.Post(PostFixture.Request(moves, setup)).Lines.ToList();

        // Quarter turn at radius 20: 31.4159 mm at 1000 mm/min → 31.831 /min.
        var length = Toolpath.MoveLength(moves[2], moves[3]);
        var operation = lines.FindIndex(l => l.StartsWith("(Operation:", StringComparison.Ordinal));
        Assert.Equal(
            [
                "G53 G0 Z-5", "G0 X0 Y0 A0", "Z30", "G1 Z20 F300", "G93", "A90 F31.831",
                $"X1 A180 F{Math.Round(1000 / length, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                "G94", "X10 F1000", "G0 Z30",
            ],
            lines.Skip(operation + 1).Take(10));
    }

    [Fact]
    public void Post_RotaryFileEndingInInverseTime_RestoresUnitsPerMinuteMode()
    {
        var setup = PostFixture.Setup(SetupOrientation.SimultaneousRotary);
        List<ToolpathMove> moves =
        [
            new(MoveKind.Rapid, 0, 0, 30, 0),
            new(MoveKind.Plunge, 0, 0, 20, 0),
            new(MoveKind.Cut, 0, 0, 20, 90),
        ];
        var lines = post.Post(PostFixture.Request(moves, setup)).Lines;

        Assert.Equal(["A90 F31.831", "G94", "G53 G0 Z-5"], lines.SkipWhile(l => l != "A90 F31.831").Take(3));
    }

    [Fact]
    public void Post_FeedFactors_SlowTheAffectedMoves()
    {
        var output = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), summary: PostFixture.Summary(10, [1, 1, 0.5f, 1, 1])));

        Assert.Equal(["G1 Z-1 F300", "X10 F500", "Y5 F1000"], output.Lines.SkipWhile(l => !l.StartsWith("G1 ", StringComparison.Ordinal)).Take(3));
    }

    [Fact]
    public void Post_WithoutSummary_EstimatesTheTimeFromFeeds()
    {
        var output = post.Post(PostFixture.Request(PostFixture.SimpleMoves()));

        // 6 mm plunge at 300, 15 mm cut at 1000, 6 mm rapid at 6000 mm/min.
        Assert.Equal((6 / 300.0 * 60) + (15 / 1000.0 * 60) + (6 / 6000.0 * 60), output.EstimatedSeconds, 9);
    }

    [Fact]
    public void Post_NamesAndComments_AreSanitized()
    {
        var setup = PostFixture.Setup();
        setup.Name = "Top side/1 (rough)";
        var output = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), setup, PostFixture.Tool("Flat 6mm (new)\nbatch"),
            project: new Project { Name = "A) B(" }));

        Assert.Equal("gcode/01_Top_side_1_rough_T01_Flat_6mm_new_batch.nc", output.FileName);
        Assert.Contains("(Project: A] B[)", output.Lines);
        Assert.Contains("(Tool: Flat 6mm [new] batch, FlatEnd, D6 mm, 2 flutes, stickout 20 mm)", output.Lines);
    }

    [Fact]
    public void Post_StockPiece_NamesThePieceAndStopsToMountItBeforeTheFirstTool()
    {
        var piece = new StockPiece(3, 1, 2, 1, -100, 50, 0, 600, 300, 18);
        var first = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), piece: piece, pieceCount: 4));
        var later = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), first: false, piece: piece, pieceCount: 4));

        const string Header = "(Piece 3 of 4: column 1, row 2, layer 1, 600 x 300 x 18 mm at X-100 Y50 Z0)";
        const string Mount = "(Mount piece 3 of 4 for setup 1: column 1, row 2, layer 1, 600 x 300 x 18 mm at X-100 Y50 Z0)";
        Assert.Equal(Header, first.Lines[3]);
        var mount = first.Lines.ToList().IndexOf(Mount);
        Assert.True(mount > 0);
        Assert.Equal("M0", first.Lines[mount + 1]);
        Assert.StartsWith("(Insert tool", first.Lines[mount + 2], StringComparison.Ordinal);
        Assert.Equal("gcode/P03_01_Top_T01_Flat_6.nc", first.FileName);
        Assert.Equal(3, first.Piece);

        Assert.Contains(Header, later.Lines);
        Assert.DoesNotContain(Mount, later.Lines);

        var whole = post.Post(PostFixture.Request(PostFixture.SimpleMoves()));
        Assert.Equal(0, whole.Piece);
        Assert.DoesNotContain(whole.Lines, l => l.Contains("iece", StringComparison.Ordinal));
    }

    [Fact]
    public void Post_ToolpathWarnings_AreReportedAndWrittenToTheHeader()
    {
        var request = PostFixture.Request(PostFixture.SimpleMoves());
        request.Operations[0].Toolpath.Warnings.Add("Stock left in a corner");
        var output = post.Post(request);

        Assert.Equal(["Contour: Stock left in a corner"], output.Warnings);
        Assert.Contains("(WARNING: Contour: Stock left in a corner)", output.Lines);
    }

    [Fact]
    public void Post_WithoutVerification_IsBlockedUnlessOverridden()
    {
        var blocked = Assert.Throws<PostException>(() => post.Post(PostFixture.Request(PostFixture.SimpleMoves(), verified: false)));
        Assert.Contains("not been verified", blocked.Message, StringComparison.Ordinal);

        var output = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), verified: false, overrideVerification: true));
        Assert.Contains(output.Warnings, w => w.Contains("overridden", StringComparison.Ordinal));
        Assert.Contains(output.Lines, l => l.StartsWith("(WARNING: Verification overridden", StringComparison.Ordinal));
    }

    [Fact]
    public void Post_VerificationErrors_BlockOnlyWhenTheyConcernThisFile()
    {
        var summary = PostFixture.Summary(10);
        var other = new VerificationReport();
        other.Issues.Add(new VerificationIssue(IssueKind.Gouge, IssueSeverity.Error, Guid.NewGuid(), 1, 1, null, null, "Gouge elsewhere"));
        other.Issues.Add(new VerificationIssue(IssueKind.FeedReduced, IssueSeverity.Warning, summary.StepId, 1, 1, null, null, "Slow"));
        post.Post(PostFixture.Request(PostFixture.SimpleMoves(), verification: other, summary: summary));

        var own = new VerificationReport();
        own.Issues.Add(new VerificationIssue(IssueKind.RapidCollision, IssueSeverity.Error, summary.StepId, 4, 4, null, null, "Rapid into stock"));
        var error = Assert.Throws<PostException>(() => post.Post(PostFixture.Request(PostFixture.SimpleMoves(), verification: own, summary: summary)));
        Assert.Contains("Rapid into stock", error.Message, StringComparison.Ordinal);

        var global = new VerificationReport();
        global.Issues.Add(new VerificationIssue(IssueKind.MissingData, IssueSeverity.Error, null, -1, -1, null, null, "No stock"));
        Assert.Throws<PostException>(() => post.Post(PostFixture.Request(PostFixture.SimpleMoves(), verification: global, summary: summary)));
        post.Post(PostFixture.Request(PostFixture.SimpleMoves(), verification: global, summary: summary, overrideVerification: true));
    }

    [Fact]
    public void Post_InvalidRequests_Throw()
    {
        var moves = PostFixture.SimpleMoves();
        Assert.Throws<PostException>(() => post.Post(PostFixture.Request(moves, rpm: 40000)));
        Assert.Throws<PostException>(() => post.Post(PostFixture.Request(moves, rpm: 1000)));

        var noPlate = PostFixture.Request(moves);
        Assert.Throws<PostException>(() => post.Post(new MillingPostRequest
        {
            Project = noPlate.Project,
            Setup = noPlate.Setup,
            Machine = noPlate.Machine,
            Tool = noPlate.Tool,
            Rpm = noPlate.Rpm,
            Operations = noPlate.Operations,
            FirstInSetup = true,
            Verification = noPlate.Verification,
        }));

        var otherTool = PostFixture.Request(moves);
        otherTool.Operations[0].Operation.ToolId = Guid.NewGuid();
        Assert.Throws<PostException>(() => post.Post(otherTool));

        var noFeed = PostFixture.Request(moves);
        noFeed.Operations[0].Conditions.PlungeRate = 0;
        Assert.Throws<PostException>(() => post.Post(noFeed));

        var noA = PostFixture.Machine();
        noA.Axes.RemoveAll(a => a.Axis == AxisName.A);
        Assert.Throws<PostException>(() => post.Post(PostFixture.Request(moves, PostFixture.Setup(SetupOrientation.Indexed), machine: noA)));

        var empty = PostFixture.Request(moves);
        Assert.Throws<PostException>(() => post.Post(new MillingPostRequest
        {
            Project = empty.Project,
            Setup = empty.Setup,
            Machine = empty.Machine,
            Tool = empty.Tool,
            Rpm = empty.Rpm,
            Operations = [],
            TouchPlate = empty.TouchPlate,
            Verification = empty.Verification,
        }));
    }

    [Fact]
    public void Post_TravelLimits_AreChecked()
    {
        var moves = PostFixture.SimpleMoves();

        // Absolute: WCS Z −1 at a work offset of Z −649.5 reaches −650.5 < −650.
        var below = Assert.Throws<PostException>(() => post.Post(PostFixture.Request(moves, workOffset: new Vector3(100, 100, -649.5f))));
        Assert.Contains("machine Z", below.Message, StringComparison.Ordinal);
        post.Post(PostFixture.Request(moves, workOffset: new Vector3(100, 100, -600)));

        // Span: 2000 mm of X on a 1300 mm axis.
        List<ToolpathMove> wide = [new(MoveKind.Rapid, 0, 0, 5), new(MoveKind.Cut, 2000, 0, 5)];
        Assert.Throws<PostException>(() => post.Post(PostFixture.Request(wide)));

        // Machine-coordinate targets.
        var safe = PostFixture.Machine();
        safe.SafeZMachine = 5;
        Assert.Throws<PostException>(() => post.Post(PostFixture.Request(moves, machine: safe)));

        // A soft limits.
        var limited = PostFixture.Machine();
        limited.GetAxis(AxisName.A)!.SoftLimits = true;
        limited.GetAxis(AxisName.A)!.MaxTravel = 180;
        List<ToolpathMove> turns = [new(MoveKind.Rapid, 0, 0, 30, 0), new(MoveKind.Cut, 0, 0, 20, 360)];
        Assert.Throws<PostException>(() => post.Post(PostFixture.Request(turns, PostFixture.Setup(SetupOrientation.SimultaneousRotary), machine: limited)));
    }

    [Fact]
    public void Post_ProbeDistance_IsClampedToTheZTravel()
    {
        var machine = PostFixture.Machine();
        machine.Probe.MaxDistance = 1000;
        var lines = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), machine: machine)).Lines;

        // Down to the table, less the 20 mm stickout.
        Assert.Equal(2, lines.Count(l => l == "G91 G38.2 Z-625 F200"));
    }

    [Fact]
    public void Post_LongCustomCode_IsRejected()
    {
        var machine = PostFixture.Machine();
        machine.Coolant.DustExtractionOnCode = "M62 P1 " + new string('X', 200);
        Assert.Throws<PostException>(() => post.Post(PostFixture.Request(PostFixture.SimpleMoves(), machine: machine)));
    }

    [Fact]
    public void Post_TurnedStock_StopsToTurnItBeforeTheFirstTool()
    {
        var setup = PostFixture.Setup();
        setup.Face = StockFace.Bottom;
        setup.Spin = 0;
        var first = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), setup)).Lines.ToList();
        var later = post.Post(PostFixture.Request(PostFixture.SimpleMoves(), setup, first: false)).Lines;

        Assert.Contains("(Setup 1: Top, G54, 3-axis, bottom face up, spin 0 deg)", first);
        var turn = first.FindIndex(l => l.StartsWith("(Turn the stock", StringComparison.Ordinal));
        Assert.Equal(
            "(Turn the stock for setup 1: bottom face up, original front face towards +Y back, original right face towards +X right; locate it in the jig centred on the dowel pins, the work offset stays)",
            first[turn]);
        Assert.Equal("M0", first[turn + 1]);
        Assert.StartsWith("(Insert tool", first[turn + 2], StringComparison.Ordinal);
        Assert.DoesNotContain(later, l => l.StartsWith("(Turn the stock", StringComparison.Ordinal));

        var top = post.Post(PostFixture.Request(PostFixture.SimpleMoves())).Lines;
        Assert.DoesNotContain(top, l => l.StartsWith("(Turn the stock", StringComparison.Ordinal));
    }

    [Fact]
    public void FromStockBounds_TurnedStock_IsTheTopCentreOfTheTurnedStock()
    {
        // 100 x 60 x 20 stock on its front face (60 tall), zero kept at the original top centre.
        var frame = new SetupFrame(SetupOrientation.ThreeAxis, 50, 10, -40, 0, StockFace.Front);
        var spot = TouchPlateSpot.FromStockBounds(frame, new Geometry.BoundingBox(new Vector3(0, 0, 0), new Vector3(100, 60, 20)));

        Assert.Equal(0, spot.X, 9);
        Assert.Equal(0, spot.Y, 9);
        Assert.Equal(40, spot.SurfaceZ, 9);
        Assert.Null(spot.BedZ);
    }

    [Fact]
    public void FromStockBounds_ThreeAxis_IsTheStockTopCentre()
    {
        // The origin X0 Y0 lies on the stock, so the bed there is not reachable for the touch plate.
        var frame = new SetupFrame(SetupOrientation.ThreeAxis, 10, 20, 30, 0);
        var spot = TouchPlateSpot.FromStockBounds(frame, new Geometry.BoundingBox(new Vector3(0, 0, 0), new Vector3(100, 60, 40)));
        Assert.Equal(new TouchPlateSpot(40, 10, 10), spot);
    }

    [Fact]
    public void FromStockBounds_OriginOffTheStock_OffersTheBedOnlyWithClearance()
    {
        // The origin is 20 mm left of and 10 mm in front of the stock: 22.4 mm from its footprint.
        var frame = new SetupFrame(SetupOrientation.ThreeAxis, -20, -10, 40, 0);
        var bounds = new Geometry.BoundingBox(new Vector3(0, 0, 0), new Vector3(100, 60, 40));

        Assert.Equal(new TouchPlateSpot(70, 40, 0, -40), TouchPlateSpot.FromStockBounds(frame, bounds, clearance: 20));
        Assert.Null(TouchPlateSpot.FromStockBounds(frame, bounds, clearance: 25).BedZ);
    }

    [Fact]
    public void FromStockBounds_Rotary_IsTheTopAboveTheAxisAtTheIndexAngle()
    {
        var bounds = new Geometry.BoundingBox(new Vector3(0, -20, -10), new Vector3(100, 20, 10));
        var level = TouchPlateSpot.FromStockBounds(new SetupFrame(SetupOrientation.Indexed, 0, 0, 0, 0), bounds);
        Assert.Equal(50, level.X, 9);
        Assert.Equal(10, level.SurfaceZ, 9);
        Assert.Null(level.BedZ);

        var turned = TouchPlateSpot.FromStockBounds(new SetupFrame(SetupOrientation.Indexed, 0, 0, 0, 90), bounds);
        Assert.Equal(20, turned.SurfaceZ, 9);

        var round = TouchPlateSpot.FromStockBounds(new SetupFrame(SetupOrientation.SimultaneousRotary, 0, 0, 0, 0), bounds, 12.5);
        Assert.Equal(new TouchPlateSpot(50, 0, 12.5), round);
    }
}
