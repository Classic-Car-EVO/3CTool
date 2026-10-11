using ILGPU;
using ILGPU.Runtime;

namespace ThreeCTool.Compute.Tests;

public class ComputeContextTests
{
    private static void Saxpy(Index1D i, float a, ArrayView<float> x, ArrayView<float> y, ArrayView<float> result) =>
        result[i] = (a * x[i]) + y[i];

    [Fact]
    public void ListDevicesIncludesCpu()
    {
        Assert.Contains(ComputeContext.ListDevices(), d => d.Backend == ComputeBackend.Cpu);
    }

    [Fact]
    public void CpuBackendRunsKernel()
    {
        using var compute = ComputeContext.Create(ComputeBackend.Cpu);
        Assert.Equal(ComputeBackend.Cpu, compute.Device.Backend);

        float[] x = [1f, 2f, 3f, 4f];
        float[] y = [10f, 20f, 30f, 40f];
        var accelerator = compute.Accelerator;
        using var xBuf = accelerator.Allocate1D(x);
        using var yBuf = accelerator.Allocate1D(y);
        using var resultBuf = accelerator.Allocate1D<float>(x.Length);

        var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, float, ArrayView<float>, ArrayView<float>, ArrayView<float>>(Saxpy);
        kernel(x.Length, 2f, xBuf.View, yBuf.View, resultBuf.View);
        accelerator.Synchronize();

        Assert.Equal([12f, 24f, 36f, 48f], resultBuf.GetAsArray1D());
    }
}
