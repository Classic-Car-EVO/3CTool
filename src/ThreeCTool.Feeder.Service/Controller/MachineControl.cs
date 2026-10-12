using System.Globalization;

using Grpc.Core;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Jobs;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.Streaming;
using ThreeCTool.Streaming.Gcode;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Feeder.Service.Controller;

/// <summary>Manual machine operations from the touch UI or desktop; refused while a job is active.</summary>
public sealed class MachineControl(ControllerSession session, JobManager jobs, SettingsStore settings, FeederOptions options) : IDisposable
{
    private const double UnboundedJogDistance = 10_000;
    private const double LimitInset = 0.5;
    private const double ProbeRetractMm = 2;

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task JogAsync(JogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var distances = new[] { request.X, request.Y, request.Z, request.A };
        if (distances.All(d => d == 0) || distances.Any(d => !double.IsFinite(d)))
        {
            throw new ArgumentException("Jog needs a finite distance on at least one axis.");
        }

        var feed = ValidFeed(request.Feed);
        await WithMachineAsync(c => c.JogAsync("G91 G21 " + Words(distances) + " F" + GcodeInterpreter.Format(feed), cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Jogs towards the travel limit (or a long distance for unlimited axes) in the given direction until the client
    /// stops sending keep-alives, then cancels the jog.
    /// </summary>
    public async Task JogContinuousAsync(IAsyncStreamReader<JogHold> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (!await requests.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var direction = requests.Current.Direction ?? throw new ArgumentException("The first message must carry the jog direction.");
        var signs = new[] { Math.Sign(direction.X), Math.Sign(direction.Y), Math.Sign(direction.Z), Math.Sign(direction.A) };
        if (signs.All(s => s == 0))
        {
            throw new ArgumentException("Jog direction is empty.");
        }

        var feed = ValidFeed(direction.Feed);
        jobs.EnsureIdle();
        var connection = session.Current;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.JogAsync(ContinuousJogCommand(connection, signs, feed), cancellationToken).ConfigureAwait(false);
            try
            {
                var next = requests.MoveNext(cancellationToken);
                while (true)
                {
                    var delay = Task.Delay(options.JogKeepAlive, cancellationToken);
                    if (await Task.WhenAny(next, delay).ConfigureAwait(false) != next || !await next.ConfigureAwait(false))
                    {
                        break;
                    }

                    next = requests.MoveNext(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or RpcException)
            {
                // Client gone: cancel below.
            }
            finally
            {
                if (connection.IsConnected)
                {
                    await connection.SendRealtimeAsync(RealtimeCommand.JogCancel, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CancelJogAsync() => await session.Current.SendRealtimeAsync(RealtimeCommand.JogCancel).ConfigureAwait(false);

    public Task HomeAsync(CancellationToken cancellationToken) => WithMachineAsync(c => c.HomeAsync(cancellationToken), cancellationToken);

    public Task UnlockAsync(CancellationToken cancellationToken) => WithMachineAsync(c => c.UnlockAsync(cancellationToken), cancellationToken);

    /// <summary>Soft reset; allowed at any time (a running job faults and becomes recoverable).</summary>
    public Task ResetAsync(CancellationToken cancellationToken) => session.Current.SoftResetAsync(null, cancellationToken);

    public Task SetWorkZeroAsync(WorkZeroRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var words = new List<string>();
        if (request.X)
        {
            words.Add("X0");
        }

        if (request.Y)
        {
            words.Add("Y0");
        }

        if (request.Z)
        {
            words.Add("Z0");
        }

        if (request.A)
        {
            words.Add("A0");
        }

        if (words.Count == 0)
        {
            throw new ArgumentException("Select at least one axis to zero.");
        }

        return WithMachineAsync(c => c.ExecuteOkAsync("G10 L20 P0 " + string.Join(' ', words), null, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Touch plate on top of the stock: probe down, set work Z to the plate thickness at the trigger point, retract.
    /// Returns the machine Z where the probe triggered.
    /// </summary>
    public async Task<double> ProbeTouchPlateAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = settings.Current;
        var thickness = request.PlateThickness > 0 ? request.PlateThickness : current.TouchPlateThickness;
        var travel = request.MaxTravel > 0 ? request.MaxTravel : current.ProbeMaxTravel;
        var feed = request.Feed > 0 ? request.Feed : current.ProbeFeed;
        if (!double.IsFinite(thickness) || thickness < 0 || !double.IsFinite(travel) || travel <= 0 || !double.IsFinite(feed))
        {
            throw new ArgumentException("Invalid probe parameters.");
        }

        double machineZ = 0;
        await WithMachineAsync(async c =>
        {
            LineResult result;
            try
            {
                result = await c.ExecuteOkAsync($"G91 G21 G38.2 Z-{GcodeInterpreter.Format(travel)} F{GcodeInterpreter.Format(feed)}",
                    ControllerQueries.LongCommandTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (c.IsConnected && !c.NeedsResync)
                {
                    await c.ExecuteAsync("G90", null, CancellationToken.None).ConfigureAwait(false);
                }
            }

            var probe = result.Responses.OfType<FeedbackMessage>().LastOrDefault(m => m.Tag == "PRB")
                ?? throw new InvalidOperationException("The controller did not report a probe result.");
            var parts = probe.Content.Split(':');
            if (parts.Length < 2 || parts[^1] != "1")
            {
                throw new InvalidOperationException("The probe did not trigger.");
            }

            var coordinates = parts[0].Split(',');
            machineZ = double.Parse(coordinates[2], NumberStyles.Float, CultureInfo.InvariantCulture);
            await c.ExecuteOkAsync("G10 L20 P0 Z" + GcodeInterpreter.Format(thickness), null, cancellationToken).ConfigureAwait(false);
            await c.ExecuteOkAsync($"G91 G0 Z{GcodeInterpreter.Format(ProbeRetractMm)}", null, cancellationToken).ConfigureAwait(false);
            await c.ExecuteOkAsync("G90", null, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return machineZ;
    }

    private async Task WithMachineAsync(Func<ControllerConnection, Task> action, CancellationToken cancellationToken)
    {
        jobs.EnsureIdle();
        var connection = session.Current;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            jobs.EnsureIdle();
            await action(connection).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private string ContinuousJogCommand(ControllerConnection connection, int[] signs, double feed)
    {
        var axes = session.Identity?.Config?.Axes() ?? [];
        var machine = connection.LastStatus?.MachinePosition;
        var reach = UnboundedJogDistance;
        var bounded = new bool[signs.Length];
        if (machine is not null)
        {
            for (var i = 0; i < 3; i++)
            {
                if (signs[i] == 0 || axes.FirstOrDefault(a => (int)a.Axis == i) is not { MaxTravel: > 0 } axis)
                {
                    continue;
                }

                var (min, max) = axis.HomingPositiveDirection
                    ? (axis.HomingMachinePosition - axis.MaxTravel, axis.HomingMachinePosition)
                    : (axis.HomingMachinePosition, axis.HomingMachinePosition + axis.MaxTravel);
                var limit = signs[i] > 0 ? max - LimitInset : min + LimitInset;
                reach = Math.Min(reach, Math.Max(0, (limit - machine[i]) * signs[i]));
                bounded[i] = true;
            }
        }

        if (bounded.Any(b => b))
        {
            if (reach < 0.01)
            {
                throw new InvalidOperationException("The axis is at its travel limit.");
            }

            var targets = new List<string>();
            for (var i = 0; i < signs.Length; i++)
            {
                if (signs[i] != 0)
                {
                    targets.Add(AxisVector.Letters[i] + GcodeInterpreter.Format(machine![i] + (signs[i] * reach)));
                }
            }

            return $"G53 G90 G21 {string.Join(' ', targets)} F{GcodeInterpreter.Format(feed)}";
        }

        return "G91 G21 " + Words(signs.Select(s => s * UnboundedJogDistance).ToArray()) + " F" + GcodeInterpreter.Format(feed);
    }

    private static string Words(double[] distances)
    {
        var words = new List<string>();
        for (var i = 0; i < distances.Length; i++)
        {
            if (distances[i] != 0)
            {
                words.Add(AxisVector.Letters[i] + GcodeInterpreter.Format(distances[i]));
            }
        }

        return string.Join(' ', words);
    }

    private static double ValidFeed(double feed) =>
        feed > 0 && double.IsFinite(feed) ? feed : throw new ArgumentException("Jog feed must be positive.");

    public void Dispose() => gate.Dispose();
}
