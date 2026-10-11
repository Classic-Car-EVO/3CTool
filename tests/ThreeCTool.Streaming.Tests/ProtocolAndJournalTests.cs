using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using ThreeCTool.Streaming.Gcode;
using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Streaming.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Parse_StatusReport_ReadsAllFields()
    {
        var message = ProtocolParser.Parse("<Run|MPos:1.000,2.000,3.000,0.000,0.000|Bf:15,120|Ln:42|FS:500,12000|WCO:100.000,100.000,-300.000,0.000,0.000|Ov:110,100,90>");

        var report = Assert.IsType<StatusMessage>(message).Report;
        Assert.Equal(MachineState.Run, report.State);
        Assert.Equal(new AxisVector(1, 2, 3, 0, 0), report.MachinePosition);
        Assert.Equal(15, report.PlannerBlocksAvailable);
        Assert.Equal(120, report.RxBytesAvailable);
        Assert.Equal(42, report.LineNumber);
        Assert.Equal(500, report.Feed);
        Assert.Equal(12000, report.SpindleSpeed);
        Assert.Equal(-300, report.WorkOffset!.Z);
        Assert.Equal(new OverrideValues(110, 100, 90), report.Overrides);
        Assert.False(report.IsMotionStopped);
    }

    [Theory]
    [InlineData("<Hold:1|MPos:0,0,0>", false)]
    [InlineData("<Hold:0|MPos:0,0,0>", true)]
    [InlineData("<Idle|MPos:0,0,0>", true)]
    [InlineData("<Alarm|MPos:0,0,0>", true)]
    [InlineData("<Jog|MPos:0,0,0>", false)]
    public void IsMotionStopped_DependsOnStateAndSubState(string text, bool stopped)
    {
        Assert.True(ProtocolParser.TryParseStatus(text, out var report));
        Assert.Equal(stopped, report.IsMotionStopped);
    }

    [Fact]
    public void Parse_LineKinds()
    {
        Assert.IsType<OkMessage>(ProtocolParser.Parse("ok"));
        Assert.Equal(20, Assert.IsType<ErrorMessage>(ProtocolParser.Parse("error:20")).Code);
        Assert.Equal(3, Assert.IsType<AlarmMessage>(ProtocolParser.Parse("ALARM:3")).Code);
        Assert.IsType<WelcomeMessage>(ProtocolParser.Parse("Grbl 3.9 [FluidNC v3.9.5 (sim) '$' for help]"));
        var feedback = Assert.IsType<FeedbackMessage>(ProtocolParser.Parse("[PRB:1.000,2.000,-3.500,0.000,0.000:1]"));
        Assert.Equal("PRB", feedback.Tag);
        Assert.True(ProtocolParser.TryParseProbe(feedback.Content, out var probe));
        Assert.True(probe.Success);
        Assert.Equal(-3.5, probe.Position.Z);
    }

    [Fact]
    public void Parse_ConfigLine_KeepsIndentation()
    {
        var text = Assert.IsType<TextMessage>(ProtocolParser.Parse("    steps_per_mm: 100.000"));
        Assert.Equal("    steps_per_mm: 100.000", text.Raw);
    }

    [Fact]
    public void TryApplyOffset_ReadsWorkCoordinateLines()
    {
        var coordinates = new WorkCoordinates();
        Assert.True(ProtocolParser.TryApplyOffset((FeedbackMessage)ProtocolParser.Parse("[G55:10.000,20.000,-30.000,0.000,0.000]"), coordinates));
        Assert.True(ProtocolParser.TryApplyOffset((FeedbackMessage)ProtocolParser.Parse("[TLO:12.500]"), coordinates));

        Assert.Equal(new AxisVector(10, 20, -30, 0, 0), coordinates.Get("G55"));
        Assert.Equal(12.5, coordinates.ToolLengthOffset);
        var copy = WorkCoordinates.Deserialize(coordinates.Serialize());
        Assert.Equal(coordinates.Get("G55"), copy.Get("G55"));
        Assert.Equal(12.5, copy.ToolLengthOffset);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(9_999_999, 9_999_999)]
    [InlineData(10_000_000, 1)]
    [InlineData(10_000_005, 6)]
    public void WireLineNumber_WrapsAtMaximum(int line, int wire) => Assert.Equal(wire, JobStreamer.WireLineNumber(line));

    [Theory]
    [InlineData(1, 10_000_005, 10_000_000)]
    [InlineData(9_999_998, 10_000_005, 9_999_998)]
    [InlineData(40, 45, 40)]
    public void FileLineFromWire_MapsBackAcrossWrap(int reported, int lastSent, int expected) =>
        Assert.Equal(expected, JobStreamer.FileLineFromWire(reported, lastSent));
}

public sealed class GcodeTests
{
    [Fact]
    public void Parse_StripsCommentsAndLineNumbers()
    {
        var block = GcodeBlock.Parse("N10 g1 x10.5 Y-2 (cut) ; tail");

        Assert.Equal(["cut", "tail"], block.Comments);
        Assert.Equal(10.5, block.Get('X'));
        Assert.True(block.HasG(1));
        Assert.Equal("G1X10.5Y-2", block.ToWire());
        Assert.Equal("N7G0Z5G1X10.5Y-2", block.ToWire(7, "G0Z5"));
    }

    [Fact]
    public void Parse_SystemCommandsDirectivesAndGarbage()
    {
        Assert.Equal("$H", GcodeBlock.Parse("$H").ToWire());

        var directive = GcodeBlock.Parse("(MC:HEAT T0 210 WAIT)");
        Assert.False(directive.HasCode);
        Assert.Equal("HEAT", directive.Directive!.Verb);
        Assert.True(directive.Directive.Wait);
        Assert.True(GcodeBlock.Parse("(MC:PROBE BEGIN)").Directive!.IsProbeBegin);

        var garbage = GcodeBlock.Parse("G1 X1..2");
        Assert.NotNull(garbage.Unparsed);
        Assert.True(garbage.HasCode);
    }

    [Theory]
    [InlineData("M0", true)]
    [InlineData("M6 T2", true)]
    [InlineData("G91 G38.2 Z-10 F100", true)]
    [InlineData("G10 L20 P1 Z0", true)]
    [InlineData("$H", true)]
    [InlineData("(MC:PROBE BEGIN)", true)]
    [InlineData("(MC:HEAT T0 210 WAIT)", true)]
    [InlineData("(MC:HEAT T0 210)", false)]
    [InlineData("G1 X10 F300", false)]
    [InlineData("M3 S10000", false)]
    public void IsBarrier_ClassifiesBlocks(string line, bool barrier) =>
        Assert.Equal(barrier, GcodeInterpreter.IsBarrier(GcodeBlock.Parse(line)));

    [Fact]
    public void Apply_TracksPositionArcsAndModes()
    {
        var interpreter = new GcodeInterpreter();
        var lines = new[] { "G21 G90 G54", "G0 X10 Y0 Z5", "G2 X20 Y0 I5 J0 F100", "G91", "G1 X5", "G90 M3 S9000 M8" };
        MotionSegment? arc = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var segment = interpreter.Apply(GcodeBlock.Parse(lines[i]), i + 1);
            if (i == 2)
            {
                arc = segment;
            }
        }

        var state = interpreter.State;
        Assert.NotNull(arc);
        Assert.True(arc.IsArc);
        Assert.Equal(Math.PI * 5, arc.Length, 6);
        Assert.Equal(15, arc.PointAt(0.5)[0], 6);
        Assert.Equal(5, arc.PointAt(0.5)[1], 6);
        Assert.Equal([25, 0, 5], state.Position[..3]);
        Assert.Equal(MotionMode.Linear, state.Motion);
        Assert.Equal(100, state.Feed);
        Assert.Equal(3, state.Spindle);
        Assert.Equal(9000, state.SpindleSpeed);
        Assert.True(state.Flood);
        Assert.Equal(1, arc.DistanceTo([15, 4, 5, 0, 0], [15, 4, 5, 0, 0]), 2);
    }

    [Fact]
    public void Apply_WorkOffsetChangesAndG28_ForgetPosition()
    {
        var interpreter = new GcodeInterpreter();
        interpreter.Apply(GcodeBlock.Parse("G90 G54 G0 X1 Y2 Z3"), 1);
        interpreter.Apply(GcodeBlock.Parse("G55"), 2);
        Assert.True(double.IsNaN(interpreter.State.Position[0]));

        interpreter.Apply(GcodeBlock.Parse("G0 X1 Y2 Z3"), 3);
        interpreter.Apply(GcodeBlock.Parse("G28"), 4);
        Assert.True(double.IsNaN(interpreter.State.Position[2]));

        interpreter.Apply(GcodeBlock.Parse("G53 G0 X50 Y60"), 5);
        Assert.True(interpreter.State.MachineAxes[0]);
        Assert.Equal(50, interpreter.State.Position[0]);
    }

    [Fact]
    public void Apply_ProbeBlock_SetsInProbeBlockAndZeroesAfterG10L20()
    {
        var interpreter = new GcodeInterpreter();
        foreach (var (line, i) in Jobs.ProbeBlock.Take(4).Select((l, i) => (l, i)))
        {
            interpreter.Apply(GcodeBlock.Parse(line), i + 1);
        }

        Assert.True(interpreter.InProbeBlock);
        Assert.True(double.IsNaN(interpreter.State.Position[2]));
        interpreter.Apply(GcodeBlock.Parse("G90"), 5);
        interpreter.Apply(GcodeBlock.Parse("G10 L20 P1 Z0"), 6);
        Assert.Equal(0, interpreter.State.Position[2]);
        interpreter.Apply(GcodeBlock.Parse("(MC:PROBE END)"), 7);
        Assert.False(interpreter.InProbeBlock);
    }
}

public sealed class JobFileAndJournalTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "3ctool-journal-" + Guid.NewGuid().ToString("N"));

    public JobFileAndJournalTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task OpenAsync_HashesAndIndexesLines()
    {
        var path = Path.Combine(folder, "job.nc");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 3000).Select(i => $"G1 X{i}"));

        var file = await JobFile.OpenAsync(path);

        Assert.Equal(3000, file.LineCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), file.Sha256, ignoreCase: true);
        Assert.Equal((2500, "G1 X2500"), file.ReadLines(2500).First());
        Assert.Equal("G1 X3000", file.ReadLine(3000));
        Assert.Null(file.ReadLine(3001));
        Assert.Equal(3000 - 1023, file.ReadLines(1024).Count());
    }

    [Fact]
    public void Record_BatchesCommits_FlushPersists()
    {
        var path = Path.Combine(folder, "journal.db");
        using (var journal = new SqliteJobJournal(path, TimeSpan.FromMinutes(1)))
        {
            journal.Record(Progress(0), flush: true);
            var commits = journal.CommitCount;
            for (var i = 1; i <= 500; i++)
            {
                journal.Record(Progress(i));
            }

            Assert.Equal(commits, journal.CommitCount);
            Assert.Equal(500, journal.Load("job1")!.Executed);

            journal.AddEvent("job1", new JobEvent(DateTimeOffset.UtcNow, JobEventKinds.Started, 1, "Started"));
            journal.AddEvent("job1", new JobEvent(DateTimeOffset.UtcNow, JobEventKinds.Alarm, 7, "ALARM:3"), flush: true);
            Assert.Equal(commits + 1, journal.CommitCount);
        }

        using var reopened = new SqliteJobJournal(path);
        var progress = reopened.Load("job1")!;
        Assert.Equal(500, progress.Executed);
        Assert.Equal(InterruptionKind.Alarm, progress.Interruption);
        Assert.Equal("1.000,2.000,3.000,0.000,0.000", progress.MachinePosition);
        Assert.Equal([JobEventKinds.Started, JobEventKinds.Alarm], reopened.GetEvents("job1").Select(e => e.Kind));
        Assert.Null(reopened.Load("other"));
    }

    [Fact]
    public async Task Record_TimerFlushesDirtyProgress()
    {
        var path = Path.Combine(folder, "journal.db");
        using var journal = new SqliteJobJournal(path, TimeSpan.FromMilliseconds(30));
        journal.Record(Progress(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (journal.CommitCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(journal.CommitCount > 0);
        using var other = new SqliteJobJournal(path);
        Assert.Equal(5, other.Load("job1")!.Executed);
    }

    private static JobProgress Progress(int executed) => new()
    {
        JobId = "job1",
        FileSha256 = "abc",
        State = JobState.Faulted,
        Interruption = InterruptionKind.Alarm,
        Executed = executed,
        MachinePosition = "1.000,2.000,3.000,0.000,0.000",
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
