using CloudEmuera.Infrastructure.Assets;

namespace CloudEmuera.Infrastructure.Tests.Assets;

[Trait("Category", "InstanceLimits")]
public sealed class PresentationAssetOptionsTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        PresentationAssetOptions.Default.Validate();
        PresentationAssetReadGate gate = new(PresentationAssetOptions.Default);
        Assert.True(gate.TryAcquire(1, out IDisposable lease));
        lease.Dispose();
        Assert.Equal(new PresentationAssetGateSnapshot(0, 0), gate.Snapshot);
    }

    [Fact]
    public void GateDoesNotAdmitMoreConcurrentReadsOrBytesThanConfigured()
    {
        PresentationAssetOptions options = new()
        {
            MaxAssetBytes = 10,
            MaxRangeBytes = 10,
            MaxConcurrentReads = 1,
            MaxInFlightBytes = 10,
        };
        PresentationAssetReadGate gate = new(options);

        Assert.True(gate.TryAcquire(10, out IDisposable first));
        Assert.False(gate.TryAcquire(1, out _));
        Assert.Equal(new PresentationAssetGateSnapshot(1, 10), gate.Snapshot);
        first.Dispose();
        Assert.True(gate.TryAcquire(10, out IDisposable second));
        second.Dispose();
        Assert.Equal(new PresentationAssetGateSnapshot(0, 0), gate.Snapshot);
    }

    [Fact]
    public void InvalidRangeAndInFlightRelationshipsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationAssetOptions
        {
            MaxAssetBytes = 10,
            MaxRangeBytes = 11,
            MaxInFlightBytes = 10,
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresentationAssetOptions
        {
            MaxAssetBytes = 10,
            MaxRangeBytes = 10,
            MaxInFlightBytes = 9,
        }.Validate());
    }
}
