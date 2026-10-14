using ThreeCTool.Compute;
using ThreeCTool.Desktop.Services;

namespace ThreeCTool.Desktop.Tests;

public sealed class ComputeServiceTests
{
    [Theory]
    [InlineData(ComputeBackend.OpenCL)]
    [InlineData(ComputeBackend.Cuda)]
    [InlineData(null)]
    public async Task AcquireFallsBackToCpuWhenTheDeviceFails(ComputeBackend? preferred)
    {
        using var compute = new ComputeService(preferred) { Factory = _ => throw new InvalidOperationException("kernel build failed") };

        using (var lease = await compute.AcquireAsync())
        {
            Assert.Equal(ComputeBackend.Cpu, lease.Context.Device.Backend);
        }

        Assert.Contains(preferred?.ToString() ?? "Auto", compute.LastError, StringComparison.Ordinal);
        Assert.Contains("kernel build failed", compute.LastError, StringComparison.Ordinal);
        Assert.EndsWith("using CPU", compute.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquireOnCpuReportsFailures()
    {
        using var compute = new ComputeService(ComputeBackend.Cpu) { Factory = _ => throw new InvalidOperationException("broken") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => compute.AcquireAsync());
    }
}

/// <summary>The whole CAM chain on the default (GPU) device, where every kernel is built in one context.</summary>
[Trait("Category", "Gpu")]
public sealed class GpuCamPipelineTests(CamProjectFixture project) : IClassFixture<CamProjectFixture>
{
    [Fact]
    public async Task RunAllOnTheDefaultDevice()
    {
        using var compute = new ComputeService();
        var (session, _, _) = await project.CreateProjectAsync("gpu");
        await using var _ = session;
        using var pipeline = new CamPipeline(session, compute);

        var outputs = await pipeline.RunAllAsync();

        Assert.NotEqual(ComputeBackend.Cpu, compute.Device?.Backend);
        Assert.Null(compute.LastError);
        Assert.NotEmpty(outputs);
    }
}
