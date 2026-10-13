using ThreeCTool.Compute;

namespace ThreeCTool.Desktop.Services;

/// <summary>
/// Owns the shared <see cref="ComputeContext"/>. Work holds a lease (one job at a time on the accelerator); changing
/// the preferred backend waits for the running job and recreates the context on next use.
/// </summary>
public sealed class ComputeService(ComputeBackend? preferred = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ComputeContext? context;
    private bool disposed;

    /// <summary>Raised before the context is disposed for a backend change: drop everything allocated on it.</summary>
    public event EventHandler? Resetting;

    public ComputeBackend? Preferred { get; private set; } = preferred;

    /// <summary>Device of the current context, null before first use.</summary>
    public ComputeDeviceInfo? Device => context?.Device;

    /// <summary>Error of the last failed context creation (shown on the diagnostics page).</summary>
    public string? LastError { get; private set; }

    public static IReadOnlyList<ComputeDeviceInfo> ListDevices()
    {
        try
        {
            return ComputeContext.ListDevices();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or TypeInitializationException or NotSupportedException)
        {
            return [];
        }
    }

    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (context is null)
            {
                try
                {
                    context = await Task.Run(() => ComputeContext.Create(Preferred), cancellationToken).ConfigureAwait(false);
                    LastError = null;
                }
                catch (Exception ex) when (Preferred is not null and not ComputeBackend.Cpu && ex is not OperationCanceledException)
                {
                    // A broken driver must not make the application unusable.
                    LastError = $"{Preferred}: {ex.Message}; using CPU";
                    context = await Task.Run(() => ComputeContext.Create(ComputeBackend.Cpu), cancellationToken).ConfigureAwait(false);
                }
            }

            return new Lease(this, context);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public async Task SetPreferredAsync(ComputeBackend? backend, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (backend == Preferred && context is not null)
            {
                return;
            }

            Preferred = backend;
            Resetting?.Invoke(this, EventArgs.Empty);
            context?.Dispose();
            context = null;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        context?.Dispose();
        context = null;
        gate.Dispose();
    }

    /// <summary>Exclusive use of the context until disposed.</summary>
    public sealed class Lease : IDisposable
    {
        private ComputeService? owner;

        internal Lease(ComputeService owner, ComputeContext context)
        {
            this.owner = owner;
            Context = context;
        }

        public ComputeContext Context { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.gate.Release();
        }
    }
}
