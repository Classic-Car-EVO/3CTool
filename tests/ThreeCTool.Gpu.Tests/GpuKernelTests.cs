using ILGPU;
using ILGPU.Runtime;
using ThreeCTool.Compute;
using Xunit.Abstractions;

namespace ThreeCTool.Gpu.Tests;

[Trait("Category", "Gpu")]
public class GpuKernelTests(ITestOutputHelper output)
{
    private const int Length = 1 << 20;

    private static void Saxpy(Index1D i, float a, ArrayView<float> x, ArrayView<float> y, ArrayView<float> result) =>
        result[i] = (a * x[i]) + y[i];

    [Fact]
    public void GpuDeviceIsAvailable()
    {
        Assert.Contains(ComputeContext.ListDevices(), d => d.Backend != ComputeBackend.Cpu);
    }

    [Fact]
    public void DefaultSelectionPrefersGpu()
    {
        using var compute = ComputeContext.Create();
        output.WriteLine($"Selected: {compute.Device}");
        Assert.NotEqual(ComputeBackend.Cpu, compute.Device.Backend);
    }

    [Fact]
    public void GpuKernelMatchesCpuReference()
    {
        using var compute = ComputeContext.Create();
        var accelerator = compute.Accelerator;

        var random = new Random(1234);
        var x = new float[Length];
        var y = new float[Length];
        for (var i = 0; i < Length; i++)
        {
            x[i] = (float)random.NextDouble();
            y[i] = (float)random.NextDouble();
        }

        using var xBuf = accelerator.Allocate1D(x);
        using var yBuf = accelerator.Allocate1D(y);
        using var resultBuf = accelerator.Allocate1D<float>(Length);

        var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, float, ArrayView<float>, ArrayView<float>, ArrayView<float>>(Saxpy);
        kernel(Length, 2.5f, xBuf.View, yBuf.View, resultBuf.View);
        accelerator.Synchronize();
        var result = resultBuf.GetAsArray1D();

        for (var i = 0; i < Length; i++)
        {
            Assert.Equal((2.5f * x[i]) + y[i], result[i], 1e-5f);
        }
    }
}
