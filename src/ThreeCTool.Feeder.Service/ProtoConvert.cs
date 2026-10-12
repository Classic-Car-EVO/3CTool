using Google.Protobuf.WellKnownTypes;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Jobs;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.Streaming.Protocol;

namespace ThreeCTool.Feeder.Service;

internal static class ProtoConvert
{
    public static Timestamp? ToTimestamp(DateTimeOffset? value) => value is { } v ? Timestamp.FromDateTimeOffset(v) : null;

    public static RunInfo ToRunInfo(RunRow run) => new()
    {
        RunId = run.Id,
        JobId = run.JobId,
        JobName = run.Header.Name,
        State = run.State,
        StartedAt = ToTimestamp(run.StartedAt),
        EndedAt = ToTimestamp(run.EndedAt),
        LastLine = run.LastLine,
        HasFeedback = run.Feedback is not null,
    };

    public static RunEvent ToRunEvent(JournalEvent e) => new()
    {
        Sequence = e.Sequence,
        RunId = e.RunId,
        At = ToTimestamp(e.Event.At),
        Kind = e.Event.Kind,
        Line = e.Event.Line ?? 0,
        Message = e.Event.Message,
    };

    public static Axes? ToAxes(AxisVector? v) => v is null ? null : new Axes { X = v.X, Y = v.Y, Z = v.Z, A = v.A };
}
