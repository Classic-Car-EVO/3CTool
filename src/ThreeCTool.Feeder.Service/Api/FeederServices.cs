using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Controller;
using ThreeCTool.Feeder.Service.Jobs;
using ThreeCTool.Feeder.Service.Security;
using ThreeCTool.Feeder.Service.Storage;

using FeederGrpc = ThreeCTool.Feeder.Contracts.Feeder;

namespace ThreeCTool.Feeder.Service.Api;

public sealed class PairingService(PairingManager pairing, FeederIdentity identity) : Pairing.PairingBase
{
    public override Task<FeederInfo> Hello(Empty request, ServerCallContext context) => Task.FromResult(identity.Info());

    public override Task<PairReply> Pair(PairRequest request, ServerCallContext context)
    {
        var (pairingId, token) = pairing.Pair(request.Code, request.ClientName);
        return Task.FromResult(new PairReply { PairingId = pairingId, Token = token, Feeder = identity.Info() });
    }
}

public sealed class FeederService(
    FeederIdentity identity,
    FeederDatabase database,
    JobStore store,
    JobManager jobs,
    MachineControl machine,
    ControllerSession session,
    SettingsStore settings,
    PromptBroker prompts,
    ObservableJournal journal,
    StatusHub status,
    ILogger<FeederService> logger) : FeederGrpc.FeederBase
{
    private static readonly Empty None = new();

    public override Task<FeederInfo> GetInfo(Empty request, ServerCallContext context) => Task.FromResult(identity.Info());

    public override async Task<JobInfo> UploadJob(IAsyncStreamReader<UploadChunk> requestStream, ServerCallContext context)
    {
        var token = context.CancellationToken;
        if (!await requestStream.MoveNext(token).ConfigureAwait(false) || requestStream.Current.PartCase != UploadChunk.PartOneofCase.Header)
        {
            throw new ArgumentException("An upload must start with the header.");
        }

        var header = requestStream.Current.Header;
        var caller = context.GetCaller();
        var row = await store.UploadAsync(header, Data(requestStream, token), token).ConfigureAwait(false);
        logger.JobUploaded(row.Header.Name, caller.Description, row.LineCount);
        return ToJobInfo(row, null);
    }

    public override Task<JobList> ListJobs(Empty request, ServerCallContext context)
    {
        var latest = database.LatestRunPerJob();
        var list = new JobList();
        list.Jobs.AddRange(store.List().Select(j => ToJobInfo(j, latest.GetValueOrDefault(j.Id))));
        return Task.FromResult(list);
    }

    public override Task<Empty> DeleteJob(JobRef request, ServerCallContext context)
    {
        var running = jobs.IsActive && jobs.Snapshot()?.JobId == request.JobId;
        if (running || database.LatestRecoverableRun()?.JobId == request.JobId)
        {
            throw new FeederStateException("The job is running or has a run that can still be recovered.");
        }

        store.Delete(request.JobId);
        return Task.FromResult(None);
    }

    public override Task<Preview> GetPreview(JobRef request, ServerCallContext context) => store.GetPreviewAsync(request.JobId, context.CancellationToken);

    public override Task<StartJobReply> StartJob(StartJobRequest request, ServerCallContext context) => jobs.StartJobAsync(request, context.CancellationToken);

    public override async Task<Empty> Pause(Empty request, ServerCallContext context)
    {
        await jobs.PauseAsync().ConfigureAwait(false);
        return None;
    }

    public override async Task<Empty> Resume(Empty request, ServerCallContext context)
    {
        await jobs.ResumeAsync().ConfigureAwait(false);
        return None;
    }

    public override async Task<Empty> Stop(Empty request, ServerCallContext context)
    {
        await jobs.StopAsync().ConfigureAwait(false);
        return None;
    }

    public override async Task<Empty> SetFeedOverride(FeedOverrideRequest request, ServerCallContext context)
    {
        await jobs.SetFeedOverrideAsync(request.Percent).ConfigureAwait(false);
        return None;
    }

    public override Task<Empty> SetSpindleRpm(SpindleRpmRequest request, ServerCallContext context)
    {
        jobs.SetSpindleRpm(request.Rpm);
        return Task.FromResult(None);
    }

    public override Task<RecoveryPlanInfo> GetRecoveryPlan(Empty request, ServerCallContext context) => jobs.GetRecoveryPlanAsync(context.CancellationToken);

    public override async Task<Empty> StartRecovery(Empty request, ServerCallContext context)
    {
        await jobs.StartRecoveryAsync(context.CancellationToken).ConfigureAwait(false);
        return None;
    }

    public override Task<Empty> DiscardRecovery(Empty request, ServerCallContext context)
    {
        jobs.DiscardRecovery();
        return Task.FromResult(None);
    }

    public override Task<Empty> AnswerPrompt(PromptAnswer request, ServerCallContext context)
    {
        prompts.Answer(request);
        return Task.FromResult(None);
    }

    public override async Task<Empty> Jog(JogRequest request, ServerCallContext context)
    {
        await machine.JogAsync(request, context.CancellationToken).ConfigureAwait(false);
        return None;
    }

    public override async Task<Empty> JogContinuous(IAsyncStreamReader<JogHold> requestStream, ServerCallContext context)
    {
        await machine.JogContinuousAsync(requestStream, context.CancellationToken).ConfigureAwait(false);
        return None;
    }

    public override async Task<Empty> CancelJog(Empty request, ServerCallContext context)
    {
        await machine.CancelJogAsync().ConfigureAwait(false);
        return None;
    }

    public override async Task<Empty> Home(Empty request, ServerCallContext context)
    {
        await machine.HomeAsync(context.CancellationToken).ConfigureAwait(false);
        return None;
    }

    public override async Task<Empty> Unlock(Empty request, ServerCallContext context)
    {
        await machine.UnlockAsync(context.CancellationToken).ConfigureAwait(false);
        return None;
    }

    public override async Task<Empty> Reset(Empty request, ServerCallContext context)
    {
        await machine.ResetAsync(context.CancellationToken).ConfigureAwait(false);
        return None;
    }

    public override async Task<Empty> SetWorkZero(WorkZeroRequest request, ServerCallContext context)
    {
        await machine.SetWorkZeroAsync(request, context.CancellationToken).ConfigureAwait(false);
        return None;
    }

    public override async Task<ProbeReply> ProbeTouchPlate(ProbeRequest request, ServerCallContext context) =>
        new() { WorkZMachine = await machine.ProbeTouchPlateAsync(request, context.CancellationToken).ConfigureAwait(false) };

    public override async Task WatchStatus(Empty request, IServerStreamWriter<FeederStatus> responseStream, ServerCallContext context)
    {
        await foreach (var snapshot in status.WatchAsync(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(snapshot, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task WatchEvents(WatchEventsRequest request, IServerStreamWriter<RunEvent> responseStream, ServerCallContext context)
    {
        var token = context.CancellationToken;
        var runId = string.IsNullOrEmpty(request.RunId) ? jobs.CurrentRunId : request.RunId;
        if (runId is null)
        {
            return;
        }

        var live = Channel.CreateUnbounded<JournalEvent>(new UnboundedChannelOptions { SingleReader = true });
        void OnEvent(object? sender, JournalEvent e)
        {
            if (e.RunId == runId)
            {
                live.Writer.TryWrite(e);
            }
        }

        journal.EventAdded += OnEvent;
        try
        {
            var last = request.After;
            foreach (var e in journal.GetNumberedEvents(runId))
            {
                if (e.Sequence > last)
                {
                    await responseStream.WriteAsync(ProtoConvert.ToRunEvent(e), token).ConfigureAwait(false);
                    last = e.Sequence;
                }
            }

            await foreach (var e in live.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (e.Sequence > last)
                {
                    await responseStream.WriteAsync(ProtoConvert.ToRunEvent(e), token).ConfigureAwait(false);
                    last = e.Sequence;
                }
            }
        }
        finally
        {
            journal.EventAdded -= OnEvent;
        }
    }

    public override Task<Empty> SubmitFeedback(FeedbackSubmission request, ServerCallContext context)
    {
        jobs.SubmitFeedback(request);
        return Task.FromResult(None);
    }

    public override Task<FeedbackList> ListFeedback(ListFeedbackRequest request, ServerCallContext context) =>
        Task.FromResult(jobs.ListFeedback(request.IncludeAcknowledged));

    public override Task<Empty> AcknowledgeFeedback(FeedbackAck request, ServerCallContext context)
    {
        jobs.AcknowledgeFeedback(request.RunIds);
        return Task.FromResult(None);
    }

    public override Task<RunList> ListRuns(ListRunsRequest request, ServerCallContext context) => Task.FromResult(jobs.ListRuns(request.JobId));

    public override Task<RunLog> GetRunLog(RunRef request, ServerCallContext context) => Task.FromResult(jobs.GetRunLog(request.RunId));

    public override async Task<ControllerConfigInfo> ReadControllerConfig(Empty request, ServerCallContext context)
    {
        var identity = session.Identity ?? throw new FeederStateException("The controller is not connected.");
        var config = jobs.IsActive ? identity.Config : await session.RefreshConfigAsync(context.CancellationToken).ConfigureAwait(false);
        var info = new ControllerConfigInfo
        {
            FirmwareVersion = identity.Firmware,
            BuildOptions = identity.BuildInfo.Options ?? string.Empty,
            ConfigText = config?.Text ?? string.Empty,
            ConfigHash = config?.Hash ?? string.Empty,
        };
        info.Warnings.AddRange(identity.Warnings);
        if (config is null)
        {
            info.Warnings.Add("The controller configuration could not be read.");
        }

        return info;
    }

    public override Task<Empty> SetImportedConfig(ImportedConfig request, ServerCallContext context)
    {
        settings.ImportedConfigHash = string.IsNullOrWhiteSpace(request.ConfigHash) ? null : request.ConfigHash.Trim().ToLowerInvariant();
        return Task.FromResult(None);
    }

    private static JobInfo ToJobInfo(JobRow job, RunRow? lastRun) => new()
    {
        JobId = job.Id,
        Header = job.Header,
        LineCount = job.LineCount,
        UploadedAt = ProtoConvert.ToTimestamp(job.UploadedAt),
        LastRunState = lastRun?.State ?? string.Empty,
        LastRunAt = ProtoConvert.ToTimestamp(lastRun?.StartedAt),
    };

    private static async IAsyncEnumerable<ByteString> Data(IAsyncStreamReader<UploadChunk> reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await reader.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            if (reader.Current.PartCase != UploadChunk.PartOneofCase.Data)
            {
                throw new ArgumentException("Only the first upload message may carry a header.");
            }

            yield return reader.Current.Data;
        }
    }
}

public sealed class LocalAdminService(
    PairingManager pairing,
    FeederIdentity identity,
    SettingsStore settings,
    JobManager jobs) : LocalAdmin.LocalAdminBase
{
    private static readonly Empty None = new();

    public override Task<PairingCode> StartPairing(Empty request, ServerCallContext context)
    {
        var code = pairing.StartPairing();
        return Task.FromResult(new PairingCode
        {
            Code = code.Code,
            ExpiresAt = ProtoConvert.ToTimestamp(code.ExpiresAt),
            CertificateFingerprint = identity.Fingerprint,
        });
    }

    public override Task<Empty> CancelPairing(Empty request, ServerCallContext context)
    {
        pairing.CancelPairing();
        return Task.FromResult(None);
    }

    public override Task<PairingList> ListPairings(Empty request, ServerCallContext context)
    {
        var list = new PairingList();
        list.Pairings.AddRange(pairing.List().Select(p => new PairingInfo
        {
            PairingId = p.Id,
            ClientName = p.ClientName,
            PairedAt = ProtoConvert.ToTimestamp(p.PairedAt),
            LastSeenAt = ProtoConvert.ToTimestamp(p.LastSeenAt),
        }));
        return Task.FromResult(list);
    }

    public override Task<Empty> RevokePairing(PairingRef request, ServerCallContext context)
    {
        if (!pairing.Revoke(request.PairingId))
        {
            throw new KeyNotFoundException("Pairing not found.");
        }

        return Task.FromResult(None);
    }

    public override Task<FeederSettings> GetSettings(Empty request, ServerCallContext context) => Task.FromResult(settings.Current);

    public override Task<FeederSettings> UpdateSettings(FeederSettings request, ServerCallContext context)
    {
        var current = settings.Current;
        var connectionChanged = request.Transport != current.Transport || request.SerialPort != current.SerialPort
            || request.BaudRate != current.BaudRate || request.Host != current.Host || request.TcpPort != current.TcpPort;
        if (connectionChanged && jobs.IsActive)
        {
            throw new FeederStateException("The controller connection cannot be changed while a job is running.");
        }

        return Task.FromResult(settings.Update(request));
    }

    public override Task<SerialPortList> ListSerialPorts(Empty request, ServerCallContext context)
    {
        var list = new SerialPortList();
        const string byId = "/dev/serial/by-id";
        if (Directory.Exists(byId))
        {
            list.Ports.AddRange(Directory.EnumerateFileSystemEntries(byId).Order(StringComparer.Ordinal));
        }

        list.Ports.AddRange(System.IO.Ports.SerialPort.GetPortNames().Order(StringComparer.Ordinal));
        return Task.FromResult(list);
    }
}
