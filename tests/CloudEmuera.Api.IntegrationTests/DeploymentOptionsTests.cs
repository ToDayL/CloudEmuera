using CloudEmuera.Api.Configuration;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Security;
using CloudEmuera.Api.Workers;
using CloudEmuera.Infrastructure.Assets;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.Ipc;
using CloudEmuera.RuntimeAdapter;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

[Trait("Category", "InstanceLimits")]
public sealed class DeploymentOptionsTests
{
    [Fact]
    [Trait("Category", "WorkerLifecycle")]
    public void HostShutdownBudgetLeavesContainerExitHeadroom()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), WorkerShutdownDefaults.HostShutdownTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), WorkerShutdownDefaults.ComposeStopGracePeriod);
        Assert.True(WorkerShutdownDefaults.ComposeStopGracePeriod > WorkerShutdownDefaults.HostShutdownTimeout);
    }

    [Fact]
    [Trait("Category", "WorkerLifecycle")]
    public void WorkerLaunchRequestRetainsConfiguredRuntimeTimeouts()
    {
        var request = new WorkerLaunchRequest(
            new WorkerBinding("sess_timeout", "wrk_timeout", 1),
            "/tmp/cloudemuera-timeout-session",
            "v18-compatible",
            RuntimeSaveLayout.Root,
            runtimeInitializationTimeout: TimeSpan.FromSeconds(47),
            runtimeExecutionTimeout: TimeSpan.FromSeconds(59));

        Assert.Equal(TimeSpan.FromSeconds(47), request.RuntimeInitializationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(59), request.RuntimeExecutionTimeout);
    }

    [Fact]
    public void BinderPrefersNewArchiveKeyAndReadsLegacyFreeSpaceKey()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CloudEmuera:Capacity:MaxArchiveBytes"] = "200",
                ["CloudEmuera:Capacity:MaxGamePackageBytes"] = "100",
                ["CloudEmuera:MinDataRootFreeBytes"] = "7",
            })
            .Build();

        InstanceCapacityOptions options = DeploymentOptionsBinder.BindCapacity(configuration, out bool legacyArchive, out bool legacyFreeSpace);

        Assert.Equal(200, options.MaxArchiveBytes);
        Assert.Equal(7, options.MinDataRootFreeBytes);
        Assert.False(legacyArchive);
        Assert.True(legacyFreeSpace);
    }

    [Fact]
    [Trait("Category", "Realtime")]
    public void BinderLoadsRealtimeOriginAllowlistFromDeploymentConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CloudEmuera:Realtime:AllowedOrigins"] = "https://game.example,http://localhost:5173",
            })
            .Build();
        RealtimeOriginPolicy policy = DeploymentOptionsBinder.BindRealtimeOriginPolicy(configuration);
        DefaultHttpContext context = new();
        context.Request.Headers.Origin = "http://localhost:5173";

        Assert.True(policy.IsConfigured);
        Assert.True(policy.IsAllowed(context));
    }

    [Fact]
    public void BinderRejectsNonDecimalConfigurationWithoutSilentFallback()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CloudEmuera:Capacity:MaxArchiveBytes"] = "2GiB",
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            DeploymentOptionsBinder.BindCapacity(configuration, out _, out _));
        Assert.Contains("MaxArchiveBytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossGroupValidatorRejectsAssetBudgetAboveSessionRoot()
    {
        InstanceCapacityOptions capacity = new()
        {
            MaxArchiveBytes = 128,
            MaxExpandedBytes = 256,
            MaxArchiveSingleFileBytes = 128,
            MaxSessionRootBytes = 256,
            MaxStagingReservedBytes = 512,
            MaxSaveFileBytes = 128,
            MinDataRootFreeBytes = 0,
        };
        PresentationAssetOptions assets = new()
        {
            MaxAssetBytes = 512,
            MaxRangeBytes = 512,
            MaxInFlightBytes = 512,
        };
        WorkerManagerOptions worker = new("/tmp/cloudemuera-options-data", "/tmp/CloudEmuera.Worker.dll");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            DeploymentOptionsValidator.Validate(
                capacity,
                RealtimeOutputOptions.Default,
                RealtimeGatewayOptions.Default,
                worker,
                assets));
        Assert.Contains("MaxAssetBytes", exception.Message, StringComparison.Ordinal);
    }
}
