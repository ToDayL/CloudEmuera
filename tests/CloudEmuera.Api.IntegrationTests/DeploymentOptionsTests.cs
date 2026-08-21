using CloudEmuera.Api.Configuration;
using CloudEmuera.Api.Realtime;
using CloudEmuera.Api.Workers;
using CloudEmuera.Infrastructure.Assets;
using CloudEmuera.Infrastructure.Capacity;
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
