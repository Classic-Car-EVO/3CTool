using System.Runtime.CompilerServices;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Controller;
using ThreeCTool.Feeder.Service.Jobs;
using ThreeCTool.Feeder.Service.Security;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Feeder.Service;

/// <summary>Builds <see cref="FeederStatus"/> snapshots and streams them on change (throttled, with a heartbeat).</summary>
public sealed class StatusHub
{
    private readonly ControllerSession session;
    private readonly JobManager jobs;
    private readonly PromptBroker prompts;
    private readonly PairingManager pairing;
    private readonly FeederOptions options;
    private readonly Lock gate = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long sequence;

    public StatusHub(ControllerSession session, JobManager jobs, PromptBroker prompts, PairingManager pairing, FeederOptions options)
    {
        this.session = session;
        this.jobs = jobs;
        this.prompts = prompts;
        this.pairing = pairing;
        this.options = options;
        session.Changed += (_, _) => Signal();
        jobs.Changed += (_, _) => Signal();
        prompts.Changed += (_, _) => Signal();
        pairing.Changed += (_, _) => Signal();
    }

    public FeederStatus Snapshot()
    {
        var status = new FeederStatus
        {
            Sequence = Interlocked.Increment(ref sequence),
            Connection = session.State,
            Job = jobs.Snapshot(),
            Prompt = prompts.Current,
            RecoveryAvailable = jobs.RecoveryAvailable,
            Recovering = jobs.Recovering,
            FeedbackRunId = jobs.FeedbackRunId ?? string.Empty,
            LastError = session.LastError ?? string.Empty,
            PairingActive = pairing.ActiveCode is not null,
            PairingCount = pairing.Count,
        };
        if (session.Identity is { } identity)
        {
            status.Controller = identity.Description;
            status.Firmware = identity.Firmware;
            status.Warnings.AddRange(identity.Warnings);
        }

        if (session.State == ConnectionState.Connected && session.LastStatus is { } report)
        {
            status.MachineState = report.State.ToString();
            status.SubState = report.SubState;
            status.MachinePosition = ProtoConvert.ToAxes(report.MachinePosition);
            status.WorkPosition = ProtoConvert.ToAxes(report.WorkPosition);
            status.WorkOffset = ProtoConvert.ToAxes(report.WorkOffset);
            status.FeedOverride = report.Overrides?.Feed ?? 100;
            status.RapidOverride = report.Overrides?.Rapid ?? 100;
            status.SpindleOverride = report.Overrides?.Spindle ?? 100;
            status.FeedRate = report.Feed ?? 0;
            status.SpindleSpeed = report.SpindleSpeed ?? 0;
            if (session.AlarmCode is { } alarm)
            {
                status.AlarmCode = alarm;
                status.AlarmText = FluidNcCodes.AlarmDescription(alarm);
            }
        }

        return status;
    }

    /// <summary>Current status, then a new one after each change (at most every <see cref="FeederOptions.StatusMinInterval"/>, at least every heartbeat).</summary>
    public async IAsyncEnumerable<FeederStatus> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Task signal;
            lock (gate)
            {
                signal = changed.Task;
            }

            yield return Snapshot();
            await Task.Delay(options.StatusMinInterval, cancellationToken).ConfigureAwait(false);
            await Task.WhenAny(signal, Task.Delay(options.StatusHeartbeat, cancellationToken)).ConfigureAwait(false);
        }
    }

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            previous = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }
}
