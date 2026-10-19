using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.OpenGL.Controls;
using Avalonia.VisualTree;

using Google.Protobuf.WellKnownTypes;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Services;
using ThreeCTool.Feeder.ViewModels;
using ThreeCTool.FluidNcSimulator;
using ThreeCTool.Rendering;

namespace ThreeCTool.Feeder.Tests.Ui;

/// <summary>The Pi touch UI against a real feeder service and simulated controller, in a headless window.</summary>
[Collection(nameof(FeederUiTests))]
public sealed class FeederUiTests
{
    [Fact]
    public async Task Connects_ListsJobs_ShowsThePreview_AndDeletesJobs()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var (lines, _) = TestJobs.Zigzag(20);
        await FeederHarness.UploadAsync(feeder.Local, lines, "bracket.nc");
        await FeederHarness.UploadAsync(feeder.Local, [.. lines, "(v2)"], "pocket.nc");

        await UiSession.RunAsync(async () =>
        {
            using var ui = FeederUi.Open(feeder.SocketPath);
            var vm = ui.ViewModel;
            await ui.WaitAsync(() => vm.IsConnected && vm.MachineState == "Idle" && vm.Jobs.Jobs.Count == 2, "connected with two jobs");
            Assert.True(vm.IsControllerConnected);
            Assert.Empty(vm.ConnectionText);
            Assert.False(vm.HasAlarm);
            Assert.NotEqual("-", vm.Work.X);
            Assert.Equal((int)FeederTab.Jobs, vm.SelectedTab);

            // Newest first, selected, with its preview loaded and drawn.
            var selected = vm.Jobs.Selected!;
            Assert.Equal("pocket.nc", selected.Name);
            Assert.Equal("Bracket · Top · 6 mm flat · 6061", selected.Details);
            Assert.StartsWith($"{lines.Count + 1} lines", selected.Summary, StringComparison.Ordinal);
            await ui.WaitAsync(() => vm.Jobs.Scene.Path.VertexCount > 0 && vm.Jobs.PreviewText.Length == 0, "preview loaded");
            Assert.True(vm.Jobs.CanStartJobs);
            Assert.Empty(vm.Jobs.StartHint);
            var preview = await ui.FindAsync<ToolpathView>("Preview");
            Assert.True(ui.CountDrawnPixels(preview) > 200, "The preview draws the toolpath.");

            await ui.WaitAsync(() => vm.Setup.FeederName == "Test feeder", "setup loaded");

            // Delete asks first; cancelling keeps the job.
            await ui.ClickAsync("DeleteButton");
            await ui.WaitAsync(() => vm.Confirm is { Danger: true }, "delete confirmation");
            vm.Confirm!.Cancel();
            await ui.WaitAsync(() => vm.Confirm is null, "confirmation closed");
            Assert.Equal(2, vm.Jobs.Jobs.Count);

            await ui.ClickAsync("DeleteButton");
            await ui.WaitAsync(() => vm.Confirm is not null, "delete confirmation");
            await ui.ClickAsync("Accept");
            await ui.WaitAsync(() => vm.Jobs.Jobs.Count == 1, "job deleted");
            Assert.Equal("bracket.nc", vm.Jobs.Selected?.Name);
            Assert.Equal("bracket.nc", Assert.Single((await feeder.Local.ListJobsAsync(new Empty())).Jobs).Header.Name);
        });
    }

    [Fact]
    public async Task StartJob_AnswerSpindlePrompt_AndSubmitFeedback()
    {
        await using var feeder = await FeederHarness.StartAsync();
        var (lines, final) = TestJobs.Zigzag(40, pauseAt: 20);
        await FeederHarness.UploadAsync(feeder.Local, lines, "pocket.nc");

        await UiSession.RunAsync(async () =>
        {
            using var ui = FeederUi.Open(feeder.SocketPath);
            var vm = ui.ViewModel;
            await ui.WaitAsync(() => vm.Jobs.Selected is not null && vm.Jobs.CanStartJobs, "job ready to start");

            await ui.ClickAsync("StartButton");
            await ui.WaitAsync(() => vm.Confirm is { Danger: false }, "start confirmation");
            Assert.Contains("6 mm flat", vm.Confirm!.Message, StringComparison.Ordinal);
            await ui.ClickAsync("Accept");

            // The M0 with the spindle on asks for the dial RPM and switches to the run page.
            await ui.WaitAsync(() => vm.Prompt is not null, "spindle prompt");
            var prompt = vm.Prompt!;
            Assert.Equal((int)FeederTab.Run, vm.SelectedTab);
            Assert.True(prompt.IsSpindle);
            Assert.Equal(12000, prompt.Rpm);
            Assert.Equal(lines.IndexOf("M0 (Check the clamps)") + 1, prompt.Prompt.Line);
            Assert.Equal("ProgramPaused", vm.Run.State);
            Assert.Equal("pocket.nc", vm.Run.JobName);
            Assert.True(vm.Run.IsActive);
            Assert.False(vm.Jobs.CanStartJobs);
            Assert.NotNull(vm.Run.Scene.Marker);

            await vm.Run.FeedUpCommand.ExecuteAsync(null);
            await ui.WaitAsync(() => vm.Run.FeedOverride == 110, "feed override 110%");

            // The RPM is entered on the on-screen keyboard.
            await ui.ClickAsync("EditRpm");
            await ui.WaitAsync(() => vm.Keyboard is { Numeric: true }, "numeric keyboard");
            var keyboard = vm.Keyboard!;
            Assert.Equal("12000", keyboard.Text);
            keyboard.Clear();
            foreach (var key in "15000")
            {
                keyboard.Press(key.ToString());
            }

            await ui.ClickAsync("Accept");
            await ui.WaitAsync(() => vm.Keyboard is null && prompt.Rpm == 15000, "RPM entered");
            Assert.Equal("15000 rpm", prompt.RpmText);

            await ui.ClickAsync("Confirm");
            await ui.WaitAsync(() => vm.Feedback is not null, "feedback form");
            Assert.Null(vm.Prompt);
            Assert.Equal("Completed", vm.Run.State);
            Assert.Equal(100, vm.Run.ProgressPercent);
            Assert.False(vm.Run.IsActive);
            Assert.Null(vm.Run.Scene.Marker);
            TestJobs.AssertPosition(final, feeder.Simulator.MachinePosition);

            var feedback = vm.Feedback!;
            Assert.Equal("pocket.nc", feedback.JobName);
            Assert.Equal("Completed", feedback.FinalState);
            feedback.Finish.Items[3].SelectCommand.Execute(null);
            feedback.Chatter.Items[4].SelectCommand.Execute(null);
            feedback.Wear.Items[2].SelectCommand.Execute(null);
            feedback.WearEvent.Items.Single(i => i.Label == "Worn").SelectCommand.Execute(null);
            Assert.True(feedback.Finish.Items[3].IsSelected);
            Assert.False(feedback.Finish.Items[4].IsSelected);

            await ui.ClickAsync("EditNotes");
            await ui.WaitAsync(() => vm.Keyboard is { Letters: true }, "text keyboard");
            vm.Keyboard!.Text = "Light burr on the left edge ";
            await ui.ClickAsync("Accept");
            await ui.WaitAsync(() => feedback.Notes == "Light burr on the left edge", "notes entered");

            await ui.ClickAsync("Submit");
            await ui.WaitAsync(() => vm.Feedback is null && !vm.HasOverlay, "feedback submitted");
        });

        var record = Assert.Single((await feeder.Local.ListFeedbackAsync(new ListFeedbackRequest())).Records);
        Assert.Equal(4, record.Feedback.FinishRating);
        Assert.Equal(5, record.Feedback.ChatterRating);
        Assert.Equal(3, record.Feedback.WearRating);
        Assert.Equal(WearEvent.Wear, record.Feedback.WearEvent);
        Assert.Equal("Light burr on the left edge", record.Feedback.Notes);
        Assert.Equal(15000, Assert.Single(record.RpmChanges).Rpm);
        Assert.Equal(110, Assert.Single(record.FeedOverrides).Percent);
    }

    [Fact]
    public async Task Question_NoButton_RunsTheNoBranch()
    {
        await using var feeder = await FeederHarness.StartAsync();
        string[] lines =
        [
            "(3C Tool test job)", "G21 G90 G94 G54", "G0 Z5", "(Probe on the bed at X0 Y0?)",
            "(MC:ASK BEGIN)", "G0 X10 Y0", "(MC:ASK ELSE)", "G0 X20 Y0", "(MC:ASK END)", "M30",
        ];
        await FeederHarness.UploadAsync(feeder.Local, lines, "question.nc");

        await UiSession.RunAsync(async () =>
        {
            using var ui = FeederUi.Open(feeder.SocketPath);
            var vm = ui.ViewModel;
            await ui.WaitAsync(() => vm.Jobs.Selected is not null && vm.Jobs.CanStartJobs, "job ready to start");
            await ui.ClickAsync("StartButton");
            await ui.WaitAsync(() => vm.Confirm is not null, "start confirmation");
            await ui.ClickAsync("Accept");

            await ui.WaitAsync(() => vm.Prompt is not null, "question");
            var prompt = vm.Prompt!;
            Assert.True(prompt.IsChoice);
            Assert.False(prompt.IsSpindle);
            Assert.Equal("Question", prompt.Title);
            Assert.Equal("Yes", prompt.ConfirmText);
            Assert.Equal("Stop job", prompt.CancelText);
            Assert.Equal("Probe on the bed at X0 Y0?", prompt.Message);

            await ui.ClickAsync("No");
            await ui.WaitAsync(() => vm.Feedback is not null, "feedback form");
            Assert.Equal("Completed", vm.Run.State);
        });

        var g54 = FeederHarness.G54;
        TestJobs.AssertPosition([g54[0] + 20, g54[1], g54[2] + 5], feeder.Simulator.MachinePosition);
    }

    [Fact]
    public async Task PauseResumeStop_ThenDiscardTheRecovery()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 20 });
        var (lines, _) = TestJobs.Zigzag();
        await FeederHarness.UploadAsync(feeder.Local, lines, "long.nc");

        await UiSession.RunAsync(async () =>
        {
            using var ui = FeederUi.Open(feeder.SocketPath);
            var vm = ui.ViewModel;
            await ui.WaitAsync(() => vm.Jobs.Selected is not null && vm.Jobs.CanStartJobs, "job ready to start");
            var start = vm.Jobs.StartCommand.ExecuteAsync(null);
            await ui.WaitAsync(() => vm.Confirm is not null, "start confirmation");
            vm.Confirm!.Accept();
            await start;

            await ui.WaitAsync(() => vm.Run.CanPause, "running");
            Assert.Equal((int)FeederTab.Run, vm.SelectedTab);
            Assert.True(vm.IsRunning);
            Assert.False(vm.Jog.CanJog);
            Assert.False(vm.Jobs.DeleteCommand.CanExecute(null));

            // Past the opening moves, so the tool position is known and the stopped run is resumable.
            await ui.WaitAsync(() => vm.Run.ProgressPercent >= 10, "past the opening moves");
            await ui.ClickAsync("PauseButton");
            await ui.WaitAsync(() => vm.Run.State == "Paused" && vm.Run.CanResume, "paused");
            await ui.ClickAsync("ResumeButton");
            await ui.WaitAsync(() => vm.Run.State == "Running", "resumed");

            await ui.ClickAsync("StopButton");
            await ui.WaitAsync(() => vm.Confirm is { Danger: true }, "stop confirmation");
            await ui.ClickAsync("Accept");
            await ui.WaitAsync(() => vm.Run.State == "Stopped" && vm.Run.RecoveryAvailable, "stopped, recoverable");
            Assert.False(vm.IsRunning);
            Assert.Null(vm.Feedback);

            // The job can be deleted, with a warning that the recovery is lost.
            Assert.True(vm.Jobs.DeleteCommand.CanExecute(null));
            var delete = vm.Jobs.DeleteCommand.ExecuteAsync(null);
            await ui.WaitAsync(() => vm.Confirm is { Danger: true }, "delete confirmation");
            Assert.Contains("can then no longer be recovered", vm.Confirm!.Message, StringComparison.Ordinal);
            vm.Confirm.Cancel();
            await delete;
            Assert.Single(vm.Jobs.Jobs);

            await ui.ClickAsync("RecoverButton");
            await ui.WaitAsync(() => vm.Recovery is not null, "recovery wizard");
            Assert.True(vm.Recovery!.Available);
            Assert.Equal("long.nc", vm.Recovery.JobName);
            Assert.Contains("resuming from line", vm.Recovery.LinesText, StringComparison.Ordinal);

            await ui.ClickAsync("Discard");
            await ui.WaitAsync(() => vm.Confirm is { Danger: true }, "discard confirmation");
            await ui.ClickAsync("Accept");
            await ui.WaitAsync(() => vm.Recovery is null && !vm.Run.RecoveryAvailable && vm.Feedback is not null, "recovery discarded");
            Assert.Equal("Stopped", vm.Feedback!.FinalState);

            await ui.ClickAsync("Skip");
            await ui.WaitAsync(() => !vm.HasOverlay, "feedback skipped");
        });

        Assert.Empty((await feeder.WaitStatusAsync(s => s.FeedbackRunId.Length == 0)).FeedbackRunId);
        Assert.True(Assert.Single((await feeder.Local.ListFeedbackAsync(new ListFeedbackRequest())).Records).Feedback.Skipped);
    }

    [Fact]
    public async Task Jog_StepAndPressAndHold_ThenSetWorkZero()
    {
        await using var feeder = await FeederHarness.StartAsync(new SimulatorOptions { TimeScale = 1 });

        await UiSession.RunAsync(async () =>
        {
            using var ui = FeederUi.Open(feeder.SocketPath);
            var vm = ui.ViewModel;
            var jog = vm.Jog;
            await ui.WaitAsync(() => jog.CanJog && jog.CanHome, "jog enabled");
            vm.ShowTab(FeederTab.Jog);
            var pad = await ui.FindAsync<Grid>("Pad");
            Button JogButton(string direction) => pad.Children.OfType<Button>().Single(b => Equals(b.Tag, direction));

            // Step jog: tap X+ with 10 mm at 3000 mm/min.
            jog.Step.Items.Single(i => i.Label == "10").SelectCommand.Execute(null);
            jog.Feed.Items.Single(i => i.Label == "3000").SelectCommand.Execute(null);
            var x = feeder.Simulator.MachinePosition[0];
            ui.Tap(JogButton("X+"));
            await ui.WaitAsync(() => vm.MachineState == "Idle" && Math.Abs(feeder.Simulator.MachinePosition[0] - x - 10) < 1e-3, "stepped 10 mm");

            // Press and hold Y+: the keep-alives keep it moving well past the feeder's 300 ms watchdog.
            jog.Continuous = true;
            jog.Feed.Items.Single(i => i.Label == "1000").SelectCommand.Execute(null);
            var y = feeder.Simulator.MachinePosition[1];
            var center = ui.Center(JogButton("Y+"));
            ui.Window.MouseDown(center, MouseButton.Left);
            await ui.WaitAsync(() => jog.IsJogging && vm.MachineState == "Jog", "jogging");
            await Task.Delay(900);
            Assert.Equal("Jog", vm.MachineState);
            Assert.True(feeder.Simulator.MachinePosition[1] > y + 5, "Still moving after three watchdog periods.");
            ui.Window.MouseUp(center, MouseButton.Left);
            await ui.WaitAsync(() => !jog.IsJogging && vm.MachineState == "Idle", "released");
            Assert.InRange(feeder.Simulator.MachinePosition[1] - y, 5, 40);

            // In continuous mode a plain tap does not step.
            await jog.JogCommand.ExecuteAsync("X+");
            Assert.Equal(x + 10, feeder.Simulator.MachinePosition[0], 3);

            var zero = jog.ZeroCommand.ExecuteAsync("XY");
            await ui.WaitAsync(() => vm.Confirm is not null, "zero confirmation");
            Assert.Contains("X, Y", vm.Confirm!.Message, StringComparison.Ordinal);
            await ui.ClickAsync("Accept");
            await zero;
            await ui.WaitAsync(() => jog.Work.X == "0.000" && jog.Work.Y == "0.000", "work zero set");
            Assert.Equal(feeder.Simulator.MachinePosition[0], feeder.Simulator.WorkOffset(0)[0], 3);
        });
    }

    [Fact]
    public async Task Setup_MaintenanceButtons_RequestRebootAfterConfirmation()
    {
        var folder = Directory.CreateTempSubdirectory("3cf-maint-").FullName;
        try
        {
            await using var feeder = await FeederHarness.StartAsync(home: false, configure: o => o.MaintenanceDirectory = folder);

            await UiSession.RunAsync(async () =>
            {
                using var ui = FeederUi.Open(feeder.SocketPath);
                var vm = ui.ViewModel;
                await ui.WaitAsync(() => vm.Setup.MaintenanceAvailable, "maintenance available");
                vm.ShowTab(FeederTab.Setup);
                await ui.FindAsync<Button>("ConsoleButton");
                await ui.FindAsync<Button>("PowerOffButton");

                await ui.ClickAsync("RebootButton");
                await ui.WaitAsync(() => vm.Confirm is { Danger: true }, "reboot confirmation");
                vm.Confirm!.Cancel();
                await ui.WaitAsync(() => vm.Confirm is null, "confirmation closed");
                Assert.Empty(Directory.GetFiles(folder));

                await ui.ClickAsync("RebootButton");
                await ui.WaitAsync(() => vm.Confirm is not null, "reboot confirmation");
                vm.Confirm!.Accept();
                await ui.WaitAsync(() => vm.Message == "Rebooting...", "reboot requested");
                Assert.True(File.Exists(Path.Combine(folder, "reboot.request")));
            });
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Setup_EditsAndSavesSettings_AndPairsADesktop()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);

        await UiSession.RunAsync(async () =>
        {
            using var ui = FeederUi.Open(feeder.SocketPath);
            var vm = ui.ViewModel;
            var setup = vm.Setup;
            await ui.WaitAsync(() => setup.FeederName == "Test feeder" && setup.Version.Length > 0 && setup.IsTcp && !setup.Dirty, "setup loaded");
            Assert.True(vm.HasAlarm);
            Assert.Equal("Alarm", vm.MachineState);
            Assert.Contains("unlock or home", vm.Jobs.StartHint, StringComparison.Ordinal);
            Assert.Equal(feeder.Fingerprint.Length > 0, setup.Fingerprint.Length > 0);
            vm.ShowTab(FeederTab.Setup);
            await ui.FindAsync<Button>("StartPairing");

            var probeFeed = setup.Machine.Single(s => s.Label == "Probe feed");
            var edit = setup.EditCommand.ExecuteAsync(probeFeed);
            await ui.WaitAsync(() => vm.Keyboard is { Numeric: true }, "numeric keyboard");
            Assert.Equal(probeFeed.Value, vm.Keyboard!.Text);
            vm.Keyboard.Clear();
            vm.Keyboard.Press("2");
            vm.Keyboard.Press("5");
            vm.Keyboard.Press("0");
            await ui.ClickAsync("Accept");
            await edit;
            Assert.Equal("250", probeFeed.Value);
            Assert.True(setup.Dirty);

            // Invalid numbers are refused.
            edit = setup.EditCommand.ExecuteAsync(probeFeed);
            await ui.WaitAsync(() => vm.Keyboard is not null, "keyboard");
            vm.Keyboard!.Text = "1.2.3";
            vm.Keyboard.Accept();
            await edit;
            Assert.Equal("250", probeFeed.Value);
            Assert.Equal("'1.2.3' is not a number.", vm.Message);

            var name = setup.General.Single(s => s.Label == "Feeder name");
            edit = setup.EditCommand.ExecuteAsync(name);
            await ui.WaitAsync(() => vm.Keyboard is { Letters: true }, "text keyboard");
            vm.Keyboard!.Text = "Mill";
            vm.Keyboard.ToggleShift();
            Assert.Equal("Q", vm.Keyboard.Rows[1][0]);
            vm.Keyboard.Space();
            vm.Keyboard.Press("F");
            Assert.False(vm.Keyboard.Shift);
            vm.Keyboard.Press("eederx");
            vm.Keyboard.Backspace();
            Assert.Equal("Mill Feeder", vm.Keyboard.Text);
            vm.Keyboard.Accept();
            await edit;

            await ui.ClickAsync("SaveButton");
            await ui.WaitAsync(() => !setup.Dirty && vm.Message == "Settings saved.", "settings saved");
            Assert.Equal("Mill Feeder", setup.FeederName);
            await ui.WaitAsync(() => vm.IsControllerConnected, "controller still connected");

            // Pairing: the code is shown on the screen and typed on the desktop.
            await ui.ClickAsync("StartPairing");
            await ui.WaitAsync(() => setup.PairingActive && setup.PairingCode.Length > 0, "pairing code shown");
            Assert.StartsWith("expires in", setup.PairingExpires, StringComparison.Ordinal);
            Assert.Equal(setup.Fingerprint, setup.PairingFingerprint);
            await ui.FindAsync<TextBlock>("PairingCode");
            var code = setup.PairingCode;
            var pairing = await FeederPairing.PairAsync(feeder.Address.ToString(), code, "Workshop PC",
                (_, seen) => Task.FromResult(CertificateFingerprint.Matches(feeder.Fingerprint, seen)));
            Assert.NotEmpty(pairing.Token);
            await ui.WaitAsync(() => !setup.PairingActive && setup.Pairings.Count == 1, "paired");
            Assert.Equal("Workshop PC", setup.Pairings[0].Name);

            var revoke = setup.RevokeCommand.ExecuteAsync(setup.Pairings[0]);
            await ui.WaitAsync(() => vm.Confirm is { Danger: true }, "revoke confirmation");
            await ui.ClickAsync("Accept");
            await revoke;
            Assert.Empty(setup.Pairings);
        });

        var settings = await feeder.Admin.GetSettingsAsync(new Empty());
        Assert.Equal(250, settings.ProbeFeed);
        Assert.Equal("Mill Feeder", settings.Name);
        Assert.Equal(TransportKind.Tcp, settings.Transport);
        Assert.Equal(feeder.Server.Port, settings.TcpPort);
        Assert.Empty((await feeder.Admin.ListPairingsAsync(new Empty())).Pairings);
    }

    [Fact]
    public async Task WithoutTheService_ShowsOfflineAndReportsErrors()
    {
        var socket = Path.Combine(Path.GetTempPath(), "3cf-missing-" + Guid.NewGuid().ToString("N")[..8], "feeder.sock");

        await UiSession.RunAsync(async () =>
        {
            using var ui = FeederUi.Open(socket);
            var vm = ui.ViewModel;
            await ui.WaitAsync(() => vm.ConnectionText.Contains('('), "connection failure shown");
            Assert.StartsWith("Feeder service not reachable", vm.ConnectionText, StringComparison.Ordinal);
            Assert.False(vm.IsConnected);
            Assert.Equal("Offline", vm.MachineState);
            Assert.False(vm.Jobs.CanStartJobs);
            Assert.Equal("Feeder service not reachable", vm.Jobs.StartHint);
            Assert.False(vm.Jog.CanJog);
            Assert.Equal(AxesText.Unknown, vm.Work);

            await vm.Jobs.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("The feeder service is not reachable.", vm.Message);
            Assert.True(vm.MessageIsError);
            await ui.ClickAsync("DismissMessage");
            Assert.False(vm.HasMessage);
        });
    }

    [Fact]
    public async Task AutoRenderer_FallsBackToSoftware_WithoutOpenGl()
    {
        await UiSession.RunAsync(async () =>
        {
            var scene = new ToolpathScene();
            scene.SetPath(new PolylinePath([0, 0, 0, 40, 0, 0, 40, 20, 0], [1, 2, 3], [0], [false]));
            var view = new ToolpathView { Scene = scene, Renderer = PreviewRenderer.Auto };
            var window = new Window { Width = 400, Height = 300, Content = view };
            window.Show();
            try
            {
                var deadline = DateTime.UtcNow + FeederHarness.Timeout;
                while (view.GetVisualDescendants().OfType<OpenGlControlBase>().Any() && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                }

                Assert.Empty(view.GetVisualDescendants().OfType<OpenGlControlBase>());
                Assert.False(view.UsingOpenGl);
            }
            finally
            {
                window.Close();
            }
        });
    }
}

[CollectionDefinition(nameof(FeederUiTests), DisableParallelization = true)]
public sealed class FeederUiTestGroup;
