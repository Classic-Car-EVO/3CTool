using System.Globalization;

using ThreeCTool.Streaming.Gcode;
using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Streaming.Recovery;

/// <summary>Operator prompts on the Pi touch UI (or desktop) during recovery.</summary>
public interface IOperatorInteraction
{
    /// <summary>Shows <paramref name="message"/>; returns false if the operator cancels recovery.</summary>
    Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken);
}

/// <summary>Where streaming continues after a successful recovery.</summary>
public sealed record ResumeInfo(int Line, string? FirstLinePrefix);

/// <summary>
/// Runs the recovery sequence of section 11.3 on the controller: reset, unlock and home if needed, verify/restore
/// work offsets, restore tool length, rebuild state and re-enter the cut. Every step is logged as a job event.
/// </summary>
public sealed class RecoveryExecutor(
    ControllerConnection connection,
    JobFile file,
    IJobJournal journal,
    string jobId,
    IOperatorInteraction operatorInteraction,
    RecoveryOptions options,
    IHostDirectiveHandler? directives = null)
{
    private static readonly TimeSpan LongTimeout = ControllerQueries.LongCommandTimeout;

    /// <summary>Executes <paramref name="plan"/>; returns null if the job was aborted or the operator cancelled.</summary>
    public async Task<ResumeInfo?> ExecuteAsync(RecoveryPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var progress = journal.Load(jobId) ?? throw new InvalidOperationException($"No persisted progress for job '{jobId}'.");
        foreach (var note in plan.Notes)
        {
            Log(note);
        }

        if (plan.Action is RecoveryAction.Abort or RecoveryAction.Unrecoverable)
        {
            if (directives is not null)
            {
                await directives.AllHeatersOffAsync(cancellationToken).ConfigureAwait(false);
                Log("Heaters switched off.");
            }

            journal.Record(progress with { State = JobState.Aborted, UpdatedAt = DateTimeOffset.UtcNow }, flush: true);
            Log(plan.Action == RecoveryAction.Abort ? "Job aborted." : "Job cannot be resumed automatically.", flush: true);
            return null;
        }

        journal.Record(progress with { State = JobState.Recovering, UpdatedAt = DateTimeOffset.UtcNow }, flush: true);
        if (!await ConfirmAsync("Recovery: make sure the work area is clear and the machine is safe to move.", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // 1. Known protocol state: soft reset (a held position is retained; an alarm stays latched).
        await connection.SoftResetAsync(null, cancellationToken).ConfigureAwait(false);
        var status = await connection.WaitForStatusAsync(_ => true, connection.Options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        Log($"Controller reset, state {status.State}.");

        // 2. Unlock and home when the position is not trusted.
        var lostPosition = status.State == MachineState.Alarm
            || (progress.Interruption is InterruptionKind.Alarm or InterruptionKind.PowerLoss
                && (progress.InterruptionCode is not { } code || FluidNcCodes.AlarmLosesPosition(code)));
        if (lostPosition)
        {
            if (options.Process == JobProcess.Fdm)
            {
                Log("FDM job: machine position was lost, print aborted.", flush: true);
                if (directives is not null)
                {
                    await directives.AllHeatersOffAsync(cancellationToken).ConfigureAwait(false);
                }

                journal.Record(progress with { State = JobState.Aborted, UpdatedAt = DateTimeOffset.UtcNow }, flush: true);
                return null;
            }

            if (!await ConfirmAsync("Machine position was lost. Unlock and home X/Y/Z now?", cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (status.State == MachineState.Alarm)
            {
                await connection.UnlockAsync(cancellationToken).ConfigureAwait(false);
                Log("Unlocked ($X).");
            }

            await connection.HomeAsync(cancellationToken).ConfigureAwait(false);
            Log("Homed ($H).", flush: true);
        }

        // 3. Work offsets: compare with the persisted copy, restore differences.
        await RestoreOffsetsAsync(progress, cancellationToken).ConfigureAwait(false);

        // 4. Tool length (cleared by the reset). On restart the file probes itself.
        if (plan.Action == RecoveryAction.Resume)
        {
            await RestoreToolLengthAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        }

        if (plan.Action == RecoveryAction.Restart)
        {
            await SendAsync("G53G0Z" + F(options.SafeZMachine), cancellationToken).ConfigureAwait(false);
            Log("Restarting the job from line 1.", flush: true);
            return new ResumeInfo(1, null);
        }

        var state = plan.State;
        var position = state.Position;
        var known = !double.IsNaN(position[0]) && !double.IsNaN(position[1]) && !double.IsNaN(position[2]);

        // 5. Re-entry: metric absolute, safe Z, WCS, A, XY.
        await SendAsync("G21G90G94", cancellationToken).ConfigureAwait(false);
        await SendAsync("G53G0Z" + F(options.SafeZMachine), cancellationToken).ConfigureAwait(false);
        await SendAsync("G" + state.Wcs.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        if (!double.IsNaN(position[3]))
        {
            await SendAsync(Move("G0", 3, state), cancellationToken).ConfigureAwait(false);
        }

        if (options.Process == JobProcess.Fdm)
        {
            if (!await RestoreFdmAsync(state, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        if (known)
        {
            if (state.MachineAxes[0] == state.MachineAxes[1])
            {
                await SendAsync(Move("G0", 0, state) + Axis(1, state), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendAsync(Move("G0", 0, state), cancellationToken).ConfigureAwait(false);
                await SendAsync(Move("G0", 1, state), cancellationToken).ConfigureAwait(false);
            }
        }

        // 6. Spindle (manual dial) and coolant / outputs.
        if (options.Process == JobProcess.Milling && state.Spindle is 3 or 4)
        {
            await SendAsync($"M{state.Spindle}S{F(state.SpindleSpeed)}", cancellationToken).ConfigureAwait(false);
            await connection.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
            var rpm = progress.SpindleRpm ?? state.SpindleSpeed;
            if (!await ConfirmAsync($"Start the spindle and set the dial to {rpm:0} RPM. Confirm when it is running at speed.", cancellationToken)
                .ConfigureAwait(false))
            {
                await SendAsync("M5", CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            journal.AddEvent(jobId, new JobEvent(DateTimeOffset.UtcNow, JobEventKinds.SpindleRpm, plan.ResumeLine, $"Spindle confirmed at {rpm:0} RPM"), flush: true);
        }

        if (state.Mist)
        {
            await SendAsync("M7", cancellationToken).ConfigureAwait(false);
        }

        if (state.Flood)
        {
            await SendAsync("M8", cancellationToken).ConfigureAwait(false);
        }

        foreach (var (port, on) in state.DigitalOutputs)
        {
            await SendAsync($"M{(on ? 64 : 65)}P{port.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
        }

        foreach (var (port, value) in state.AnalogOutputs)
        {
            await SendAsync($"M68E{port.ToString(CultureInfo.InvariantCulture)}Q{F(value)}", cancellationToken).ConfigureAwait(false);
        }

        // 7. Approach: rapid to a clearance above the resume point (never above safe Z), then plunge at reduced feed.
        if (known)
        {
            double approach;
            if (state.MachineAxes[2])
            {
                approach = Math.Min(position[2] + options.ApproachClearanceMm, options.SafeZMachine);
            }
            else
            {
                var live = await connection.WaitForStatusAsync(s => s.WorkOffset is not null, connection.Options.CommandTimeout, cancellationToken)
                    .ConfigureAwait(false);
                approach = Math.Min(position[2] + options.ApproachClearanceMm, options.SafeZMachine - live.WorkOffset!.Z);
            }

            var g53 = state.MachineAxes[2] ? "G53" : string.Empty;
            await SendAsync($"{g53}G0Z{F(approach)}", cancellationToken).ConfigureAwait(false);
            await SendAsync($"{g53}G1Z{F(position[2])}F{F(options.PlungeFeedMmPerMin)}", cancellationToken).ConfigureAwait(false);
            Log($"Re-entered at X{F(position[0])} Y{F(position[1])} Z{F(position[2])}.");
        }

        // 8. Restore the file's modal state.
        var modal = (state.Inches ? "G20" : "G21") + (state.Incremental ? "G91" : "G90") + (state.ArcAbsolute ? "G90.1" : "G91.1")
            + "G" + state.Plane.ToString(CultureInfo.InvariantCulture) + (state.InverseTime ? "G93" : "G94");
        if (!state.InverseTime && state.Feed > 0)
        {
            modal += "F" + F(state.Feed);
        }

        await SendAsync(modal, cancellationToken).ConfigureAwait(false);
        await connection.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        Log($"Resuming at line {plan.ResumeLine}{(plan.FirstLinePrefix is null ? string.Empty : " with " + plan.FirstLinePrefix)}.", flush: true);
        return new ResumeInfo(plan.ResumeLine, plan.FirstLinePrefix);
    }

    private static string F(double value) => GcodeInterpreter.Format(value);

    private static string Axis(int axis, ModalState state) =>
        double.IsNaN(state.Position[axis]) ? string.Empty : ModalState.AxisLetters[axis] + F(state.Position[axis]);

    /// <summary>A move of one axis (plus any following in the same frame) using G53 for machine-coordinate axes.</summary>
    private static string Move(string motion, int axis, ModalState state) =>
        (state.MachineAxes[axis] ? "G53" : string.Empty) + motion + Axis(axis, state);

    private async Task RestoreOffsetsAsync(JobProgress progress, CancellationToken cancellationToken)
    {
        var current = await connection.QueryWorkCoordinatesAsync(cancellationToken).ConfigureAwait(false);
        if (progress.WorkCoordinates is null)
        {
            Log("No persisted work offsets to verify.");
            return;
        }

        var saved = WorkCoordinates.Deserialize(progress.WorkCoordinates);
        var restored = 0;
        for (var i = 0; i < WorkCoordinates.WorkSystems.Length; i++)
        {
            var name = WorkCoordinates.WorkSystems[i];
            if (saved.Get(name) is not { } expected)
            {
                continue;
            }

            if (current.Get(name) is { } actual && actual.MaxDifference(expected) <= options.OffsetToleranceMm)
            {
                continue;
            }

            var words = string.Concat(Enumerable.Range(0, Math.Min(expected.Count, ModalState.AxisCount))
                .Select(a => ModalState.AxisLetters[a] + F(expected[a])));
            await SendAsync($"G10L2P{(i + 1).ToString(CultureInfo.InvariantCulture)}{words}", cancellationToken).ConfigureAwait(false);
            Log($"{name} restored to {expected} (controller had {current.Get(name)?.ToString() ?? "none"}).", flush: true);
            restored++;
        }

        if (restored == 0)
        {
            Log("Work offsets verified.");
        }
    }

    private async Task RestoreToolLengthAsync(RecoveryPlan plan, JobProgress progress, CancellationToken cancellationToken)
    {
        if (options.Process == JobProcess.Milling && plan.Probe is { } probe)
        {
            var message = $"Re-probing the tool length (file lines {probe.BeginLine}-{probe.EndLine}). Spindle must be stopped.";
            if (!await ConfirmAsync(message, cancellationToken).ConfigureAwait(false))
            {
                throw new OperationCanceledException("Recovery cancelled by the operator.");
            }

            var s = probe.StateBefore;
            await SendAsync("G53G0Z" + F(options.SafeZMachine), cancellationToken).ConfigureAwait(false);
            await SendAsync((s.Inches ? "G20" : "G21") + (s.Incremental ? "G91" : "G90") + "G" + s.Wcs.ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            foreach (var (_, text) in file.ReadLines(probe.BeginLine).TakeWhile(l => l.Line <= probe.EndLine))
            {
                // Operator pauses inside the routine are replaced by the confirmations here.
                var block = GcodeBlock.Parse(text);
                if (block.HasCode && !block.HasM(0) && !block.HasM(1))
                {
                    await connection.ExecuteOkAsync(block.ToWire(), LongTimeout, cancellationToken).ConfigureAwait(false);
                }
            }

            var after = await connection.QueryWorkCoordinatesAsync(cancellationToken).ConfigureAwait(false);
            Log($"Tool length re-probed: TLO {F(after.ToolLengthOffset)} (before interruption {F(progress.ToolLengthOffset ?? 0)}).", flush: true);
            return;
        }

        var tlo = progress.ToolLengthOffset ?? plan.State.ToolLengthOffset;
        if (tlo is { } value && value != 0)
        {
            await SendAsync("G43.1Z" + F(value), cancellationToken).ConfigureAwait(false);
            Log(options.Process == JobProcess.Milling
                ? $"No probe routine in the file: restored the recorded tool length offset {F(value)} (tool must not have been touched)."
                : $"Nozzle offset {F(value)} restored.", flush: true);
        }
    }

    private async Task<bool> RestoreFdmAsync(ModalState state, CancellationToken cancellationToken)
    {
        if (options.FdmParkMachine is { } park)
        {
            await SendAsync($"G53G0X{F(park.X)}Y{F(park.Y)}", cancellationToken).ConfigureAwait(false);
        }

        if (state.Heaters.Count > 0)
        {
            if (directives is null)
            {
                throw new InvalidOperationException("FDM recovery needs a heater handler.");
            }

            foreach (var (heater, temperature) in state.Heaters)
            {
                var directive = HostDirective.TryParse($"MC:HEAT {heater} {temperature.ToString(CultureInfo.InvariantCulture)} WAIT")!;
                Log($"Heating {heater} to {temperature.ToString(CultureInfo.InvariantCulture)} °C.");
                await directives.ExecuteAsync(directive, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!await ConfirmAsync("Nozzle is at temperature. Prime the nozzle and continue the print?", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (double.IsNaN(state.Position[4]))
        {
            throw new InvalidOperationException("Extruder (B) position at the resume line is unknown.");
        }

        await SendAsync($"G91G1B{F(options.FdmPrimeMm)}F{F(options.FdmPrimeFeedMmPerMin)}", cancellationToken).ConfigureAwait(false);
        await SendAsync("G90", cancellationToken).ConfigureAwait(false);
        await connection.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("G92B" + F(state.Position[4]), cancellationToken).ConfigureAwait(false);
        Log($"Primed {F(options.FdmPrimeMm)} mm; extruder set to B{F(state.Position[4])}.");
        return true;
    }

    private async Task SendAsync(string line, CancellationToken cancellationToken)
    {
        await connection.ExecuteOkAsync(line, LongTimeout, cancellationToken).ConfigureAwait(false);
        journal.AddEvent(jobId, new JobEvent(DateTimeOffset.UtcNow, JobEventKinds.Recovery, null, "> " + line));
    }

    private async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        Log("Operator: " + message);
        var confirmed = await operatorInteraction.ConfirmAsync(message, cancellationToken).ConfigureAwait(false);
        Log(confirmed ? "Confirmed." : "Cancelled by the operator.", flush: !confirmed);
        return confirmed;
    }

    private void Log(string message, bool flush = false) =>
        journal.AddEvent(jobId, new JobEvent(DateTimeOffset.UtcNow, JobEventKinds.Recovery, null, message), flush);
}
