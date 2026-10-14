using Grpc.Core;

using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Rendering;

using FeederClient = ThreeCTool.Feeder.Contracts.Feeder.FeederClient;

namespace ThreeCTool.Feeder.ViewModels;

public enum FeederTab
{
    Jobs,
    Run,
    Jog,
    Setup,
}

/// <summary>What the page view models need from the main view model: clients, status, dialogs and error reporting.</summary>
public interface IFeederShell
{
    FeederClient Client { get; }

    LocalAdmin.LocalAdminClient Admin { get; }

    /// <summary>Latest status; null while the feeder service is not reachable.</summary>
    FeederStatus? Status { get; }

    /// <summary>
    /// True on the feeder's local socket (touch screen, direct desktop connection): settings, pairing and operator
    /// prompts are only available there; a paired desktop only watches and controls jobs.
    /// </summary>
    bool IsLocal { get; }

    PreviewRenderer Renderer { get; }

    /// <summary>Runs a call; an <see cref="RpcException"/> is shown to the operator and false returned.</summary>
    Task<bool> RunAsync(Func<Task> action);

    /// <summary>On-screen keyboard; null when cancelled.</summary>
    Task<string?> RequestTextAsync(string title, string initial, bool numeric);

    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false);

    void ShowMessage(string message);

    /// <summary>Tool path preview of a job (cached); null when it cannot be loaded.</summary>
    Task<PolylinePath?> GetPreviewAsync(string jobId);

    void ShowTab(FeederTab tab);

    Task OpenRecoveryAsync();

    void CloseOverlay(object overlay);
}
