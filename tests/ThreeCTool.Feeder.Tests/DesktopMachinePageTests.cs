using Google.Protobuf.WellKnownTypes;

using ThreeCTool.Desktop.Services;
using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.Feeder.Tests.Ui;
using ThreeCTool.Feeder.ViewModels;

using DesktopConfirm = ThreeCTool.Desktop.ViewModels.ConfirmViewModel;
using DesktopMain = ThreeCTool.Desktop.ViewModels.MainViewModel;
using FileType = ThreeCTool.Desktop.ViewModels.FileType;
using IFileDialogs = ThreeCTool.Desktop.ViewModels.IFileDialogs;

namespace ThreeCTool.Feeder.Tests;

/// <summary>The desktop Machine page against a feeder paired over TLS (the remote, non-local case).</summary>
public sealed class DesktopMachinePageTests
{
    [Fact]
    public Task PairedFeeder_PairsConnectsImportsSyncsAndForgets() => UiSession.RunAsync(async () =>
    {
        await using var feeder = await FeederHarness.StartAsync();
        using var services = new AppServices(new AppPaths(Path.Combine(feeder.Folder, "desktop")));
        var main = new DesktopMain(services, new NoFileDialogs());
        var page = main.Machine;
        try
        {
            await page.ShowAsync();
            Assert.True(Assert.Single(page.Connections).IsDirect);

            // A declined fingerprint cancels pairing.
            var code = await feeder.Admin.StartPairingAsync(new Empty());
            page.StartPairingCommand.Execute(null);
            Assert.True(page.IsPairing);
            page.PairAddress = feeder.Address.ToString();
            page.PairCode = code.Code;
            page.PairName = "Workshop PC";
            var declined = page.PairCommand.ExecuteAsync(null);
            await AnswerAsync(main, confirm: false);
            await declined;
            Assert.Equal("Pairing cancelled.", main.Message);
            Assert.True(page.IsPairing);
            Assert.Single(page.Connections);

            code = await feeder.Admin.StartPairingAsync(new Empty());
            page.PairCode = code.Code;
            var pair = page.PairCommand.ExecuteAsync(null);
            var check = await AnswerAsync(main, confirm: true);
            Assert.Contains(CertificateFingerprint.Format(code.CertificateFingerprint), check.Message, StringComparison.Ordinal);
            Assert.Contains("Test feeder", check.Message, StringComparison.Ordinal);
            await pair;
            Assert.False(page.IsPairing);
            Assert.Equal(2, page.Connections.Count);
            var paired = page.SelectedConnection!.Feeder!;
            Assert.Equal("Test feeder", paired.Name);
            Assert.Equal(paired.Id, Assert.Single(await services.Feeders.ListAsync()).Id);

            page.LinkedMachine = page.Machines[0];
            var machineId = page.Machines[0].Id;
            await WaitAsync(async () => (await services.Feeders.GetAsync(paired.Id))!.MachineProfileId == machineId, "machine link saved");

            // Connected remotely: status and info work, local-only settings and pairing are not loaded.
            await page.ConnectCommand.ExecuteAsync(null);
            var remote = page.Feeder!;
            Assert.False(remote.IsLocal);
            Assert.False(remote.Setup.IsLocal);
            Assert.False(page.IsDirectConnected);
            Assert.False(page.ApplyMachineSettingsCommand.CanExecute(null));
            await WaitAsync(() => Task.FromResult(remote.IsControllerConnected && remote.Setup.FeederName == "Test feeder"), "remote status and info");
            Assert.Empty(remote.Setup.Pairings);
            Assert.False(remote.HasMessage, remote.Message);

            // Prompts are answered at the machine only.
            var (lines, _) = TestJobs.Zigzag(20, pauseAt: 2);
            var job = await FeederHarness.UploadAsync(remote.Client, lines);
            var run = await remote.Client.StartJobAsync(new StartJobRequest { JobId = job.JobId });
            Assert.True(run.Started);
            await WaitAsync(() => Task.FromResult(remote.Overlay is PromptViewModel), "prompt shown on the desktop");
            var prompt = (PromptViewModel)remote.Overlay!;
            Assert.False(prompt.CanAnswerHere);
            Assert.False(prompt.ConfirmCommand.CanExecute(null));
            Assert.Equal("Completed", (await feeder.RunToEndAsync(run.RunId)).Job.State);
            await feeder.Local.SubmitFeedbackAsync(new FeedbackSubmission { RunId = run.RunId, FinishRating = 4 });

            await page.RefreshRunsCommand.ExecuteAsync(null);
            var row = Assert.Single(page.Runs);
            Assert.Equal(run.RunId, row.Info.RunId);
            page.SelectedRun = row;
            await WaitAsync(() => Task.FromResult(page.RunLog.Count > 0), "run log loaded");

            // Controller configuration into the linked profile, recorded on the feeder.
            var import = page.ImportConfigCommand.ExecuteAsync(null);
            await AnswerAsync(main, confirm: true);
            await import;
            var config = await feeder.Local.ReadControllerConfigAsync(new Empty());
            Assert.Equal(config.ConfigHash, (await services.Library.GetMachineAsync(machineId))!.Controller.ConfigHash);
            Assert.Equal(config.ConfigHash, feeder.Service<SettingsStore>().ImportedConfigHash);

            await page.SyncFeedbackCommand.ExecuteAsync(null);
            Assert.StartsWith("Imported 1 run(s)", page.SyncSummary, StringComparison.Ordinal);
            Assert.NotNull((await services.Feeders.GetAsync(paired.Id))!.LastSyncAt);
            Assert.Equal(paired.Id, page.SelectedConnection!.Feeder!.Id);

            var forget = page.ForgetCommand.ExecuteAsync(null);
            await AnswerAsync(main, confirm: true);
            await forget;
            Assert.Null(page.Feeder);
            Assert.Single(page.Connections);
            Assert.Empty(await services.Feeders.ListAsync());
        }
        finally
        {
            main.Dispose();
        }
    });

    private static async Task<DesktopConfirm> AnswerAsync(DesktopMain main, bool confirm)
    {
        await WaitAsync(() => Task.FromResult(main.Overlay is DesktopConfirm), "confirmation shown");
        var dialog = (DesktopConfirm)main.Overlay!;
        (confirm ? dialog.ConfirmCommand : dialog.CancelCommand).Execute(null);
        return dialog;
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + FeederHarness.Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Not reached: " + what);
            }

            await Task.Delay(20);
        }
    }

    private sealed class NoFileDialogs : IFileDialogs
    {
        public Task<string?> OpenFileAsync(string title, IReadOnlyList<FileType> types) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileType> types) => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }
}
