using System.Numerics;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Services;
using ThreeCTool.Feeder.ViewModels;
using ThreeCTool.Rendering;

namespace ThreeCTool.Feeder.Tests.Ui;

public sealed class FeederUiUnitTests
{
    [Fact]
    public void Options_ParseTheKioskCommandLine()
    {
        var defaults = FeederUiOptions.Parse([]);
        Assert.False(defaults.Drm);
        Assert.Null(defaults.DrmCard);
        Assert.Equal(1, defaults.Scaling);
        Assert.Equal(FeederDefaults.UnixSocketPath, defaults.SocketPath);
        Assert.Equal(PreviewRenderer.Auto, defaults.Renderer);

        var kiosk = FeederUiOptions.Parse(["--drm", "--card", "/dev/dri/card1", "--scale", "1.5", "--socket", "/tmp/f.sock", "--renderer", "software"]);
        Assert.Equal(new FeederUiOptions { Drm = true, DrmCard = "/dev/dri/card1", Scaling = 1.5, SocketPath = "/tmp/f.sock", Renderer = PreviewRenderer.Software }, kiosk);
        Assert.Equal(PreviewRenderer.OpenGl, FeederUiOptions.Parse(["--renderer", "GL"]).Renderer);

        Assert.Throws<ArgumentException>(() => FeederUiOptions.Parse(["--fullscreen"]));
        Assert.Throws<ArgumentException>(() => FeederUiOptions.Parse(["--card"]));
        Assert.Throws<ArgumentException>(() => FeederUiOptions.Parse(["--card", "--drm"]));
        Assert.Throws<ArgumentException>(() => FeederUiOptions.Parse(["--scale", "8"]));
        Assert.Throws<ArgumentException>(() => FeederUiOptions.Parse(["--scale", "big"]));
        Assert.Throws<ArgumentException>(() => FeederUiOptions.Parse(["--renderer", "vulkan"]));
        Assert.Contains("--drm", FeederUiOptions.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keyboard_TypesShiftsAndReturnsTheText()
    {
        var keyboard = new KeyboardViewModel("Name", "ab", numeric: false);
        Assert.True(keyboard.Letters);
        Assert.Equal("q", keyboard.Rows[1][0]);
        keyboard.ToggleShift();
        Assert.Equal("Q", keyboard.Rows[1][0]);
        keyboard.Press("C");
        Assert.False(keyboard.Shift);
        Assert.Equal("q", keyboard.Rows[1][0]);
        keyboard.Space();
        keyboard.Backspace();
        keyboard.Press(new string('x', KeyboardViewModel.MaxLength));
        Assert.Equal("abC", keyboard.Text);
        keyboard.Accept();
        Assert.Equal("abC", await keyboard.Result);

        var numeric = new KeyboardViewModel("Feed", "100", numeric: true);
        Assert.Equal(["7", "8", "9"], numeric.Rows[0]);
        numeric.Clear();
        Assert.Empty(numeric.Text);
        numeric.Cancel();
        numeric.Accept();
        Assert.Null(await numeric.Result);
    }

    [Fact]
    public async Task Confirm_AndChoiceGroups()
    {
        var confirm = new ConfirmViewModel("Stop", "Stop?", "Stop", danger: true);
        confirm.Cancel();
        Assert.False(await confirm.Result);

        var group = new ChoiceGroup("Step", [(0.1, "0.1"), (1, "1"), (10, "10")], 1);
        Assert.Equal([false, true, false], group.Items.Select(i => i.IsSelected));
        group.Items[2].SelectCommand.Execute(null);
        Assert.Equal(10, group.Value);
        Assert.Equal([false, false, true], group.Items.Select(i => i.IsSelected));
    }

    [Fact]
    public void Display_FormatsAndConverts()
    {
        Assert.Equal("-1.235", Display.Mm(-1.23456));
        Assert.Equal("90.00°", Display.Degrees(90));
        Assert.Equal("12000", Display.Number(12000));
        Assert.Equal("0:05", Display.Duration(4.6));
        Assert.Equal("2:03", Display.Duration(123));
        Assert.Equal("1:00:00", Display.Duration(3600));
        Assert.Equal("0:00", Display.Duration(-5));
        Assert.True(Display.TryParse(" 2,5 ", out var value));
        Assert.Equal(2.5, value);
        Assert.False(Display.TryParse("abc", out _));
        Assert.False(Display.TryParse("NaN", out _));
        Assert.False(Display.TryParse(null, out _));

        Assert.Equal(new AxesText("1.000", "2.000", "3.000", "4.00°"), AxesText.From(new Axes { X = 1, Y = 2, Z = 3, A = 4 }));
        Assert.Same(AxesText.Unknown, AxesText.From(null));

        // A part rotated by A=90: work (Y=0, Z=10) is part Y=10 (the preview applies -A about X).
        var p = Display.PartFrame(new Axes { X = 5, Y = 0, Z = 10, A = 90 });
        Assert.True(Vector3.Distance(new Vector3(5, 10, 0), p) < 1e-4, p.ToString());
    }
}
