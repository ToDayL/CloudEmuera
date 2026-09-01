using CloudEmuera.Infrastructure.Capacity;

namespace CloudEmuera.Infrastructure.Tests.Capacity;

[Trait("Category", "ArchiveQuota")]
public sealed class InstanceCapacityOptionsTests
{
    [Fact]
    public void DefaultInstanceCapacityIsValid()
    {
        InstanceCapacityOptions.Default.Validate();
        Assert.Equal(8, InstanceCapacityOptions.DefaultMaxActiveWorkers);
        Assert.Equal(64, InstanceCapacityOptions.DefaultMaxInactiveSessions);
        Assert.Equal(8L * 1024 * 1024 * 1024, InstanceCapacityOptions.DefaultMaxArchiveBytes);
        Assert.Equal(16L * 1024 * 1024 * 1024, InstanceCapacityOptions.DefaultMaxExpandedBytes);
        Assert.Equal(1_000_000, InstanceCapacityOptions.DefaultMaxArchiveEntryCount);
        Assert.Equal(InstanceCapacityOptions.DefaultMaxArchiveBytes, InstanceCapacityOptions.Default.MaxArchiveBytes);
        Assert.Equal(1_000_000, InstanceCapacityOptions.Default.MaxSessionRootFileCount);
        Assert.Equal(4_096, InstanceCapacityOptions.Default.MaxSaveListedFiles);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void ActiveWorkerLimitMustStayWithinSupportedBounds(int value)
    {
        InstanceCapacityOptions options = new() { MaxActiveWorkers = value };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void InactiveSessionLimitMustStayWithinSupportedBounds(int value)
    {
        InstanceCapacityOptions options = new() { MaxInactiveSessions = value };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void ByteAndFreeSpaceLimitsRejectInvalidValues()
    {
        InstanceCapacityOptions[] invalid =
        [
            new() { MaxGamePackageBytes = 0 },
            new() { MaxExpandedBytes = 0 },
            new() { MaxArchiveSingleFileBytes = 0 },
            new() { MaxArchiveEntryCount = 0 },
            new() { MaxArchiveEntryCount = InstanceCapacityOptions.AbsoluteMaxArchiveEntryCount + 1 },
            new() { MaxSessionRootBytes = 0 },
            new() { MaxSessionRootFileCount = 0 },
            new() { MaxStagingReservedBytes = 0 },
            new() { MaxSaveFileBytes = 0 },
            new() { MaxSaveListedFiles = 0 },
            new() { MaxSaveListBytes = 0 },
            new() { MaxSaveFileBytes = InstanceCapacityOptions.DefaultMaxSessionRootBytes + 1 },
            new() { MinDataRootFreeBytes = -1 },
        ];

        Assert.All(invalid, options => Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate()));
    }

    [Fact]
    public void ArchiveAndSessionRelationshipsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstanceCapacityOptions
        {
            MaxArchiveSingleFileBytes = 11,
            MaxExpandedBytes = 10,
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstanceCapacityOptions
        {
            MaxExpandedBytes = 11,
            MaxSessionRootBytes = 10,
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstanceCapacityOptions
        {
            MaxArchiveBytes = 8,
            MaxExpandedBytes = 8,
            MaxStagingReservedBytes = 15,
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstanceCapacityOptions
        {
            MaxSaveFileBytes = 11,
            MaxSessionRootBytes = 10,
        }.Validate());
    }
}
