using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Services;

namespace ThreeCTool.Feeder.ViewModels;

/// <summary>Jog pad (step or press-and-hold), homing, unlock/reset, work zero and touch-plate probing.</summary>
public sealed partial class JogViewModel(IFeederShell shell) : ViewModelBase, IDisposable
{
    /// <summary>Keep-alive period for continuous jogs (the feeder cancels after 500 ms without one).</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMilliseconds(150);

    private ContinuousJog? continuous;

    public ChoiceGroup Step { get; } = new("Step", [(0.01, "0.01"), (0.1, "0.1"), (1, "1"), (10, "10"), (50, "50")], 1);

    public ChoiceGroup Feed { get; } = new("Feed mm/min", [(100, "100"), (500, "500"), (1000, "1000"), (3000, "3000")], 1000);

    /// <summary>Press-and-hold jogging instead of fixed steps.</summary>
    [ObservableProperty]
    public partial bool Continuous { get; set; }

    [ObservableProperty]
    public partial AxesText Work { get; private set; } = AxesText.Unknown;

    [ObservableProperty]
    public partial AxesText Machine { get; private set; } = AxesText.Unknown;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JogCommand), nameof(ZeroCommand), nameof(ProbeCommand))]
    public partial bool CanJog { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(HomeCommand), nameof(UnlockCommand))]
    public partial bool CanHome { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand), nameof(CancelJogCommand))]
    public partial bool IsConnected { get; private set; }

    [ObservableProperty]
    public partial bool IsJogging { get; private set; }

    public void Apply(FeederStatus? status)
    {
        Work = AxesText.From(status?.WorkPosition);
        Machine = AxesText.From(status?.MachinePosition);
        var jobActive = (status?.Job is { } job && RunViewModel.IsActiveState(job.State)) || status?.Recovering == true;
        IsConnected = status?.Connection == ConnectionState.Connected;
        CanHome = IsConnected && !jobActive && status!.MachineState is "Idle" or "Alarm";
        CanJog = IsConnected && !jobActive && status!.MachineState is "Idle" or "Jog";
        if (!CanJog && continuous is not null && status?.MachineState != "Jog")
        {
            StopContinuous();
        }
    }

    /// <summary>One step in the given direction, e.g. <c>X+</c> or <c>A-</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanJog))]
    private Task JogAsync(string direction)
    {
        if (Continuous)
        {
            // Continuous jogs are driven by press/release (StartContinuous/StopContinuous).
            return Task.CompletedTask;
        }

        var request = Direction(direction, Step.Value);
        request.Feed = Feed.Value;
        return shell.RunAsync(async () => await shell.Client.JogAsync(request));
    }

    /// <summary>Starts a press-and-hold jog; it runs until <see cref="StopContinuous"/> or the keep-alives stop.</summary>
    public void StartContinuous(string direction)
    {
        if (!Continuous || !CanJog)
        {
            return;
        }

        StopContinuous();
        var request = Direction(direction, 1);
        request.Feed = Feed.Value;
        continuous = new ContinuousJog(shell, request);
        IsJogging = true;
    }

    public void StopContinuous()
    {
        continuous?.Dispose();
        continuous = null;
        IsJogging = false;
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task CancelJogAsync()
    {
        StopContinuous();
        await shell.RunAsync(async () => await shell.Client.CancelJogAsync(new()));
    }

    [RelayCommand(CanExecute = nameof(CanHome))]
    private async Task HomeAsync()
    {
        if (await shell.ConfirmAsync("Home", "Home all axes? Make sure the tool can move freely to the home position.", "Home"))
        {
            await shell.RunAsync(async () => await shell.Client.HomeAsync(new()));
        }
    }

    [RelayCommand(CanExecute = nameof(CanHome))]
    private async Task UnlockAsync() => await shell.RunAsync(async () => await shell.Client.UnlockAsync(new()));

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ResetAsync()
    {
        StopContinuous();
        await shell.RunAsync(async () => await shell.Client.ResetAsync(new()));
    }

    /// <summary>Sets the current position as work zero for the given axes, e.g. <c>XY</c> or <c>Z</c>.</summary>
    [RelayCommand(CanExecute = nameof(CanJog))]
    private async Task ZeroAsync(string axes)
    {
        ArgumentException.ThrowIfNullOrEmpty(axes);
        if (!await shell.ConfirmAsync("Set work zero", $"Set the current position as work zero for {string.Join(", ", axes.ToCharArray())}?", "Set zero"))
        {
            return;
        }

        var request = new WorkZeroRequest { X = axes.Contains('X', StringComparison.Ordinal), Y = axes.Contains('Y', StringComparison.Ordinal), Z = axes.Contains('Z', StringComparison.Ordinal), A = axes.Contains('A', StringComparison.Ordinal) };
        await shell.RunAsync(async () => await shell.Client.SetWorkZeroAsync(request));
    }

    [RelayCommand(CanExecute = nameof(CanJog))]
    private async Task ProbeAsync()
    {
        if (!await shell.ConfirmAsync("Probe work Z",
                "Place the touch plate on the stock under the tool and attach the clip. The tool moves down until it touches the plate.",
                "Probe"))
        {
            return;
        }

        ProbeReply? reply = null;
        if (await shell.RunAsync(async () => reply = await shell.Client.ProbeTouchPlateAsync(new ProbeRequest())) && reply is not null)
        {
            shell.ShowMessage(string.Create(CultureInfo.InvariantCulture, $"Work Z set (machine Z {reply.WorkZMachine:0.000}). Remove the touch plate."));
        }
    }

    public void Dispose() => StopContinuous();

    private static JogRequest Direction(string direction, double distance)
    {
        if (direction is not { Length: 2 } || direction[1] is not ('+' or '-') || "XYZA".IndexOf(direction[0], StringComparison.Ordinal) < 0)
        {
            throw new ArgumentException($"Invalid jog direction '{direction}'.", nameof(direction));
        }

        var value = direction[1] == '+' ? distance : -distance;
        return direction[0] switch
        {
            'X' => new JogRequest { X = value },
            'Y' => new JogRequest { Y = value },
            'Z' => new JogRequest { Z = value },
            _ => new JogRequest { A = value },
        };
    }

    /// <summary>An open JogContinuous call sending keep-alives until disposed.</summary>
    private sealed class ContinuousJog : IDisposable
    {
        private readonly CancellationTokenSource stop = new();

        public ContinuousJog(IFeederShell shell, JogRequest direction)
        {
            _ = shell.RunAsync(() => RunAsync(shell, direction, stop.Token));
        }

        public void Dispose()
        {
            stop.Cancel();
            stop.Dispose();
        }

        private static async Task RunAsync(IFeederShell shell, JogRequest direction, CancellationToken token)
        {
            using var call = shell.Client.JogContinuous(cancellationToken: CancellationToken.None);
            await call.RequestStream.WriteAsync(new JogHold { Direction = direction }, CancellationToken.None);
            try
            {
                while (!token.IsCancellationRequested && !call.ResponseAsync.IsCompleted)
                {
                    await Task.Delay(KeepAliveInterval, token);
                    await call.RequestStream.WriteAsync(new JogHold(), CancellationToken.None);
                }

                await call.RequestStream.CompleteAsync();
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
            {
                // Released, or the call already ended (its result is reported below).
                if (!call.ResponseAsync.IsCompleted)
                {
                    await call.RequestStream.CompleteAsync();
                }
            }

            try
            {
                await call;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
            }
        }
    }
}
