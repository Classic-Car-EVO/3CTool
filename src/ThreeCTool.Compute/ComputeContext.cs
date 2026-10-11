using System.Collections.Concurrent;

using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute;

public enum ComputeBackend
{
    Cuda,
    OpenCL,
    Cpu,
}

public sealed record ComputeDeviceInfo(ComputeBackend Backend, string Name, long MemoryBytes);

/// <summary>Owns the ILGPU context and the selected accelerator (CUDA, then OpenCL, then CPU).</summary>
public sealed class ComputeContext : IDisposable
{
    private static readonly ComputeBackend[] DefaultOrder = [ComputeBackend.Cuda, ComputeBackend.OpenCL, ComputeBackend.Cpu];

    private readonly Context _context;
    private readonly ConcurrentDictionary<System.Reflection.MethodInfo, Delegate> _kernels = new();

    private ComputeContext(Context context, Accelerator accelerator, ComputeDeviceInfo device)
    {
        _context = context;
        Accelerator = accelerator;
        Device = device;
    }

    public Accelerator Accelerator { get; }

    public ComputeDeviceInfo Device { get; }

    /// <summary>Serializes launches: an accelerator stream is not safe for concurrent use from several threads.</summary>
    internal Lock Gate { get; } = new();

    /// <summary>Device memory the compute layer allows itself per batch (the 8060S shares system memory).</summary>
    internal long BatchBudgetBytes => Math.Clamp(Device.MemoryBytes / 8, 64L << 20, 1L << 30);

    /// <summary>Creates a context on <paramref name="preferred"/> if available, otherwise on the best available backend.</summary>
    public static ComputeContext Create(ComputeBackend? preferred = null)
    {
        ComputeBackend[] order = preferred is { } p ? [p, .. DefaultOrder.Where(b => b != p)] : DefaultOrder;
        var context = Context.Create(builder => builder.Default());
        try
        {
            foreach (var backend in order)
            {
                var device = context.Devices
                    .Where(d => ToBackend(d.AcceleratorType) == backend)
                    .OrderByDescending(d => d.MemorySize)
                    .FirstOrDefault();
                if (device is not null)
                {
                    return new ComputeContext(context, device.CreateAccelerator(context), ToInfo(device, backend));
                }
            }
        }
        catch
        {
            context.Dispose();
            throw;
        }

        context.Dispose();
        throw new InvalidOperationException("No compute device available.");
    }

    public static IReadOnlyList<ComputeDeviceInfo> ListDevices()
    {
        using var context = Context.Create(builder => builder.Default());
        return [.. context.Devices
            .Select(d => (Device: d, Backend: ToBackend(d.AcceleratorType)))
            .Where(x => x.Backend is not null)
            .Select(x => ToInfo(x.Device, x.Backend!.Value))];
    }

    public void Dispose()
    {
        Accelerator.Dispose();
        _context.Dispose();
    }

    /// <summary>Items per batch so that <paramref name="bytesPerItem"/> of per-item buffers stays within the budget.</summary>
    internal int BatchSize(long bytesPerItem) =>
        (int)Math.Clamp(BatchBudgetBytes / Math.Max(bytesPerItem, 1), 1024, 1 << 24);

    internal void Upload<T>(ArrayView1D<T, Stride1D.Dense> view, ReadOnlySpan<T> data)
        where T : unmanaged =>
        view.SubView(0, data.Length).CopyFromCPU(Accelerator.DefaultStream, data);

    internal void Download<T>(ArrayView1D<T, Stride1D.Dense> view, Span<T> destination)
        where T : unmanaged =>
        view.SubView(0, destination.Length).CopyToCPU(Accelerator.DefaultStream, destination);

    /// <summary>Compiled kernel for <paramref name="method"/>, cached per accelerator.</summary>
    internal Action<Index1D, T> Kernel<T>(Action<Index1D, T> method)
        where T : struct =>
        (Action<Index1D, T>)_kernels.GetOrAdd(method.Method, _ => Accelerator.LoadAutoGroupedStreamKernel(method));

    private static ComputeBackend? ToBackend(AcceleratorType type) => type switch
    {
        AcceleratorType.Cuda => ComputeBackend.Cuda,
        AcceleratorType.OpenCL => ComputeBackend.OpenCL,
        AcceleratorType.CPU => ComputeBackend.Cpu,
        _ => null,
    };

    private static ComputeDeviceInfo ToInfo(Device device, ComputeBackend backend) =>
        new(backend, device.Name, device.MemorySize);
}
