using ThreeCTool.Core;
using ThreeCTool.Core.Machines;
using ThreeCTool.Core.Projects;

using Xunit;

namespace ThreeCTool.Core.Tests;

public sealed class UnitsTests
{
    [Theory]
    [InlineData("6", 6.0)]
    [InlineData("6mm", 6.0)]
    [InlineData(" 6.35 MM ", 6.35)]
    [InlineData(".5 cm", 5.0)]
    [InlineData("1.3 m", 1300.0)]
    [InlineData("0.25in", 6.35)]
    [InlineData("1/4\"", 6.35)]
    [InlineData("1/8 inch", 3.175)]
    [InlineData("1 1/2 in", 38.1)]
    public void TryParseLength_ParsesSupportedFormats(string text, double expectedMm)
    {
        Assert.True(Units.TryParseLength(text, LengthUnit.Millimeter, out var mm));
        Assert.Equal(expectedMm, mm, 9);
    }

    [Fact]
    public void TryParseLength_UsesDefaultUnitWhenNoneGiven()
    {
        Assert.True(Units.TryParseLength("2", LengthUnit.Inch, out var mm));
        Assert.Equal(50.8, mm, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("6 ft")]
    [InlineData("1/0 in")]
    [InlineData("-3")]
    public void TryParseLength_RejectsInvalidInput(string? text)
    {
        Assert.False(Units.TryParseLength(text, LengthUnit.Millimeter, out _));
    }

    [Theory]
    [InlineData(LengthUnit.Millimeter)]
    [InlineData(LengthUnit.Centimeter)]
    [InlineData(LengthUnit.Meter)]
    [InlineData(LengthUnit.Inch)]
    public void Conversion_RoundTrips(LengthUnit unit)
    {
        Assert.Equal(12.7, Units.ToMillimeters(Units.FromMillimeters(12.7, unit), unit), 9);
    }
}

public sealed class SpindleConfigTests
{
    [Fact]
    public void ConstantTorqueCurve_InterpolatesBelowRatedAndCapsAbove()
    {
        var spindle = new SpindleConfig { PowerCurve = SpindleConfig.ConstantTorqueCurve(1500, 30000, 2200, 24000) };

        Assert.Equal(2200.0 * 1500 / 24000, spindle.PowerAt(1500), 6);
        Assert.Equal(1100, spindle.PowerAt(12000), 6);
        Assert.Equal(2200, spindle.PowerAt(24000), 6);
        Assert.Equal(2200, spindle.PowerAt(28000), 6);
        Assert.Equal(spindle.PowerAt(1500), spindle.PowerAt(500), 6);
    }

    [Fact]
    public void EmptyCurve_FallsBackToConstantTorque()
    {
        var spindle = new SpindleConfig();
        Assert.Equal(550, spindle.PowerAt(6000), 6);
        Assert.Equal(2200, spindle.PowerAt(30000), 6);
    }
}

public sealed class TolerancesTests
{
    [Fact]
    public void ForScale_ScalesBaselineButKeepsFinishStockZero()
    {
        var baseline = Tolerances.ForScale(1);
        var foam = Tolerances.ForScale(4);

        Assert.Equal(new Tolerances().RoughingTolerance, baseline.RoughingTolerance, 9);
        Assert.Equal(4 * baseline.FinishingTolerance, foam.FinishingTolerance, 9);
        Assert.Equal(4 * baseline.MaxScallopHeight, foam.MaxScallopHeight, 9);
        Assert.Equal(0, foam.FinishingStockToLeave);
    }
}
