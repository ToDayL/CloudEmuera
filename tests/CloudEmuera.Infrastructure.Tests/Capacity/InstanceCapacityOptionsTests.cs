using CloudEmuera.Infrastructure.Capacity;

namespace CloudEmuera.Infrastructure.Tests.Capacity;

[Trait("Category", "ArchiveQuota")]
public sealed class InstanceCapacityOptionsTests
{
    [Fact]
    public void DefaultInstanceCapacityIsValid()
    {
        InstanceCapacityOptions.Default.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void ActiveWorkerLimitMustStayWithinSupportedBounds(int value)
    {
        InstanceCapacityOptions options = new() { MaxActiveWorkers = value };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void ByteAndFreeSpaceLimitsRejectInvalidValues()
    {
        InstanceCapacityOptions[] invalid =
        [
            new() { MaxGamePackageBytes = 0 },
            new() { MaxSessionRootBytes = 0 },
            new() { MaxStagingReservedBytes = 0 },
            new() { MaxSaveFileBytes = 0 },
            new() { MaxSaveFileBytes = InstanceCapacityOptions.DefaultMaxSessionRootBytes + 1 },
            new() { MinDataRootFreeBytes = -1 },
        ];

        Assert.All(invalid, options => Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate()));
    }
}
