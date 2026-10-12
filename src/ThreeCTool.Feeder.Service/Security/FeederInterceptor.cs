using Grpc.Core;
using Grpc.Core.Interceptors;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Feeder.Service.Storage;
using ThreeCTool.Streaming;

namespace ThreeCTool.Feeder.Service.Security;

/// <summary>Marks connections accepted on the local Unix socket (touch UI); set by Kestrel connection middleware.</summary>
public sealed class LocalSocketFeature
{
    public static readonly LocalSocketFeature Instance = new();

    public static IConnectionBuilder Mark(IConnectionBuilder builder) =>
        builder.Use(next => connection =>
        {
            connection.Features.Set(Instance);
            return next(connection);
        });
}

/// <summary>Who is calling: the local touch UI, or a paired desktop over TLS.</summary>
public sealed record Caller(bool IsLocal, PairingRow? Pairing)
{
    public string Description => IsLocal ? "touch screen" : $"desktop '{Pairing?.ClientName}'";
}

public static class CallerExtensions
{
    private const string Key = "3ctool.caller";

    public static Caller GetCaller(this ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.UserState.TryGetValue(Key, out var value) && value is Caller caller ? caller : new Caller(false, null);
    }

    internal static void SetCaller(this ServerCallContext context, Caller caller) => context.UserState[Key] = caller;
}

/// <summary>A request that is valid but not allowed in the current state (maps to FAILED_PRECONDITION).</summary>
public sealed class FeederStateException(string message) : Exception(message);

/// <summary>
/// Section 11.5 access rules plus exception mapping. Pairing is open; LocalAdmin and prompt answers are local only;
/// everything else needs the Unix socket or a valid bearer token on TLS.
/// </summary>
public sealed class FeederInterceptor(PairingManager pairing, ILogger<FeederInterceptor> logger) : Interceptor
{
    private static readonly string PairingPrefix = "/" + Pairing.Descriptor.FullName + "/";
    private static readonly string LocalAdminPrefix = "/" + LocalAdmin.Descriptor.FullName + "/";
    private static readonly string AnswerPromptMethod = "/" + Contracts.Feeder.Descriptor.FullName + "/AnswerPrompt";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (Map(ex, context) is { } mapped)
        {
            throw mapped;
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        try
        {
            return await continuation(requestStream, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (Map(ex, context) is { } mapped)
        {
            throw mapped;
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (Map(ex, context) is { } mapped)
        {
            throw mapped;
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (Map(ex, context) is { } mapped)
        {
            throw mapped;
        }
    }

    private void Authorize(ServerCallContext context)
    {
        var http = context.GetHttpContext();
        var isLocal = http.Features.Get<LocalSocketFeature>() is not null;
        if (context.Method.StartsWith(PairingPrefix, StringComparison.Ordinal))
        {
            context.SetCaller(new Caller(isLocal, null));
            return;
        }

        if (isLocal)
        {
            context.SetCaller(new Caller(true, null));
            return;
        }

        if (context.Method.StartsWith(LocalAdminPrefix, StringComparison.Ordinal) || context.Method == AnswerPromptMethod)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Only available on the feeder's touch screen."));
        }

        var header = context.RequestHeaders.GetValue(FeederDefaults.AuthorizationHeader);
        var token = header is not null && header.StartsWith(FeederDefaults.BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[FeederDefaults.BearerPrefix.Length..].Trim()
            : null;
        var paired = pairing.Authenticate(token);
        if (paired is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "This desktop is not paired with the feeder (or the pairing was revoked)."));
        }

        context.SetCaller(new Caller(false, paired));
    }

    private RpcException? Map(Exception exception, ServerCallContext context)
    {
        switch (exception)
        {
            case RpcException:
                return null;
            case OperationCanceledException when context.CancellationToken.IsCancellationRequested:
                return new RpcException(new Status(StatusCode.Cancelled, "Call cancelled."));
            case PairingException pairingError:
                return new RpcException(new Status(
                    pairingError.Failure switch
                    {
                        PairingFailure.WrongCode => StatusCode.PermissionDenied,
                        PairingFailure.TooManyAttempts => StatusCode.ResourceExhausted,
                        _ => StatusCode.FailedPrecondition,
                    },
                    pairingError.Message));
            case FeederStateException:
                return new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
            case ArgumentException or FormatException:
                return new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
            case KeyNotFoundException:
                return new RpcException(new Status(StatusCode.NotFound, exception.Message));
            case ControllerDisconnectedException:
                return new RpcException(new Status(StatusCode.Unavailable, "Controller not connected: " + exception.Message));
            case ControllerCommandException:
                return new RpcException(new Status(StatusCode.Aborted, exception.Message));
            case TimeoutException:
                return new RpcException(new Status(StatusCode.DeadlineExceeded, exception.Message));
            case InvalidOperationException:
                return new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
            default:
                logger.UnhandledError(exception, context.Method);
                return new RpcException(new Status(StatusCode.Internal, "Internal feeder error; see the service log."));
        }
    }
}
