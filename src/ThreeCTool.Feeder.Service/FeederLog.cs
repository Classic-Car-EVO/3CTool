using ThreeCTool.Streaming.Jobs;
using ThreeCTool.Streaming.Recovery;

namespace ThreeCTool.Feeder.Service;

internal static partial class FeederLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {Controller} (FluidNC {Firmware}, {Blocks} planner blocks)")]
    public static partial void ControllerConnected(this ILogger logger, string controller, string firmware, int blocks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Controller {Controller} disconnected")]
    public static partial void ControllerDisconnected(this ILogger logger, string controller);

    [LoggerMessage(Level = LogLevel.Information, Message = "Controller connection failed: {Reason}")]
    public static partial void ControllerConnectFailed(this ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feeder is shutting down; stopping run {RunId}")]
    public static partial void StoppingOnShutdown(this ILogger logger, string runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Maintenance requested on the touch screen: {Action}")]
    public static partial void MaintenanceRequested(this ILogger logger, string action);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting job {Job} as run {RunId}")]
    public static partial void RunStarting(this ILogger logger, string job, string runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovering run {RunId} ({Action} at line {Line})")]
    public static partial void RunRecovering(this ILogger logger, string runId, RecoveryAction action, long line);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recovery of run {RunId} failed")]
    public static partial void RecoveryFailed(this ILogger logger, Exception exception, string runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId} failed")]
    public static partial void RunFailed(this ILogger logger, Exception exception, string runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} ended: {State} at line {Line}")]
    public static partial void RunEnded(this ILogger logger, string runId, JobState state, long line);

    [LoggerMessage(Level = LogLevel.Error, Message = "Program pause prompt failed")]
    public static partial void PromptFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} was unfinished at startup; marked {State}")]
    public static partial void RunUnfinished(this ILogger logger, string runId, string state);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error in {Method}")]
    public static partial void UnhandledError(this ILogger logger, Exception exception, string method);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Name} uploaded by {Caller} ({Lines} lines)")]
    public static partial void JobUploaded(this ILogger logger, string name, string caller, long lines);
}
