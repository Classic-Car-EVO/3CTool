using ThreeCTool.Feeder.Service;
using ThreeCTool.Feeder.Service.Jobs;
using ThreeCTool.Streaming.Jobs;

namespace ThreeCTool.Feeder.Tests;

public sealed class JobAnalysisTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "3cf-analysis-" + Guid.NewGuid().ToString("N"));

    public JobAnalysisTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TimeTable_CoversFeedsArcsDwellsAndInverseTime()
    {
        var file = await WriteAsync(
            "G21 G90 G94",
            "G0 X0 Y0 Z0",
            "G0 Z5",
            "G1 Z0 F600",
            "G1 X100 F1200",
            "G4 P2",
            "M3 S10000",
            "M0 (flip)",
            "G2 X100 Y20 I0 J10 F600",
            "M5",
            "M1",
            "G93 G1 X0 F30",
            "M30");

        var (analysis, _) = JobAnalysis.Analyze(file, new FeederOptions { RapidRateMmPerMin = 6000 });

        // The first move starts from an unknown position and is not timed.
        Assert.Equal(13, analysis.LineCount);
        Assert.Equal(0, analysis.SecondsThrough(2), 6);
        Assert.Equal(0.05, analysis.SecondsThrough(3), 3);
        Assert.Equal(0.55, analysis.SecondsThrough(4), 3);
        Assert.Equal(5.55, analysis.SecondsThrough(5), 3);
        Assert.Equal(7.55, analysis.SecondsThrough(6), 3);
        Assert.Equal(7.55, analysis.SecondsThrough(8), 3);
        Assert.Equal(7.55 + Math.PI, analysis.SecondsThrough(9), 2);
        Assert.Equal(9.55 + Math.PI, analysis.SecondsThrough(12), 2);
        Assert.Equal(analysis.SecondsThrough(13), analysis.TotalSeconds);
        Assert.Equal(analysis.TotalSeconds, analysis.SecondsThrough(1000));
        Assert.Equal([8, 11], analysis.PauseLines);
        var prompt = Assert.Single(analysis.SpindlePrompts);
        Assert.Equal(8, prompt.Key);
        Assert.Equal(10000, prompt.Value);
    }

    [Fact]
    public async Task Preview_MarksRapidsAndTessellatesArcs()
    {
        var file = await WriteAsync(
            "G21 G90",
            "G0 X0 Y0 Z5",
            "G1 Z0 F600",
            "G1 X100",
            "G2 X100 Y20 I0 J10",
            "G0 Z5",
            "M30");

        var (_, preview) = JobAnalysis.Analyze(file, new FeederOptions());

        Assert.False(preview.Rotary);
        Assert.Equal(preview.StripStarts.Count, preview.StripRapid.Count);
        Assert.Contains(true, preview.StripRapid);
        Assert.Contains(false, preview.StripRapid);
        Assert.Equal(0, preview.BoundsMin[0], 3);
        Assert.Equal(100, preview.BoundsMax[0], 3);
        Assert.Equal(20, preview.BoundsMax[1], 3);
        var arcVertices = Enumerable.Range(0, preview.Lines.Count).Where(i => preview.Lines[i] == 5).ToList();
        Assert.InRange(arcVertices.Count, 30, 40);
        Assert.Equal(90, arcVertices.Min(i => preview.Positions[3 * i]), 2);
        Assert.Equal(preview.Positions.Count, preview.Lines.Count * 3);
    }

    [Fact]
    public async Task Preview_RotaryMoves_AreInThePartFrame()
    {
        var file = await WriteAsync("G21 G90", "G0 X0 Y0 Z10 A0", "G1 A90 F3600", "M30");

        var (_, preview) = JobAnalysis.Analyze(file, new FeederOptions());

        Assert.True(preview.Rotary);
        var rotary = Enumerable.Range(0, preview.Lines.Count).Where(i => preview.Lines[i] == 3).ToList();
        Assert.InRange(rotary.Count, 44, 47);
        foreach (var i in rotary)
        {
            var y = preview.Positions[(3 * i) + 1];
            var z = preview.Positions[(3 * i) + 2];
            Assert.Equal(10, Math.Sqrt((y * y) + (z * z)), 3);
        }

        // At A90 the tool at Z10 sits at -Y (or +Y) in the part frame.
        var last = rotary[^1];
        Assert.Equal(10, Math.Abs(preview.Positions[(3 * last) + 1]), 3);
        Assert.Equal(0, preview.Positions[(3 * last) + 2], 3);
    }

    [Fact]
    public async Task Preview_IsDecimatedToTheVertexBudget()
    {
        var (lines, _) = TestJobs.Zigzag(4000);
        var file = await WriteAsync([.. lines]);

        var (_, preview) = JobAnalysis.Analyze(file, new FeederOptions { PreviewMaxVertices = 1000 });

        var vertices = preview.Lines.Count;
        Assert.InRange(vertices, 200, 1100);
        Assert.Equal(0, preview.BoundsMin[0], 3);
        Assert.Equal(40, preview.BoundsMax[0], 3);
        Assert.Equal(2000, preview.BoundsMax[1], 3);
        Assert.True(preview.SourceSegments > 4000);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var (lines, _) = TestJobs.Zigzag(30, pauseAt: 10);
        var file = await WriteAsync([.. lines]);
        var (analysis, _) = JobAnalysis.Analyze(file, new FeederOptions());
        var path = Path.Combine(folder, "job.analysis");

        analysis.Save(path);
        var loaded = JobAnalysis.Load(path);

        Assert.Equal(analysis.LineCount, loaded.LineCount);
        Assert.Equal(analysis.TotalSeconds, loaded.TotalSeconds);
        Assert.Equal(analysis.PauseLines, loaded.PauseLines);
        Assert.Equal(analysis.SpindlePrompts, loaded.SpindlePrompts);
        for (var line = 0; line <= analysis.LineCount; line++)
        {
            Assert.Equal(analysis.SecondsThrough(line), loaded.SecondsThrough(line));
        }

        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Throws<InvalidDataException>(() => JobAnalysis.Load(path));
    }

    private async Task<JobFile> WriteAsync(params string[] lines)
    {
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".nc");
        await File.WriteAllLinesAsync(path, lines);
        return await JobFile.OpenAsync(path);
    }
}
