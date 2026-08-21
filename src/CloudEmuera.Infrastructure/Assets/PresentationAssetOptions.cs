namespace CloudEmuera.Infrastructure.Assets;

/// <summary>
/// Instance-wide bounds for presentation manifest and asset responses. The
/// gate counts declared bytes, not a byte[] copy, so a slow client cannot turn
/// a collection of otherwise valid streams into unbounded memory pressure.
/// </summary>
public sealed record PresentationAssetOptions
{
    public const long DefaultMaxManifestBytes = 4L * 1024 * 1024;
    public const long DefaultMaxAssetBytes = 512L * 1024 * 1024;
    public const long DefaultMaxRangeBytes = 64L * 1024 * 1024;
    public const int DefaultMaxConcurrentReads = 32;
    public const long DefaultMaxInFlightBytes = 512L * 1024 * 1024;

    public const long AbsoluteMaxManifestBytes = 64L * 1024 * 1024;
    public const long AbsoluteMaxAssetBytes = 4L * 1024 * 1024 * 1024;
    public const long AbsoluteMaxInFlightBytes = 16L * 1024 * 1024 * 1024;
    public const int AbsoluteMaxConcurrentReads = 4096;

    public long MaxManifestBytes { get; init; } = DefaultMaxManifestBytes;
    public long MaxAssetBytes { get; init; } = DefaultMaxAssetBytes;
    public long MaxRangeBytes { get; init; } = DefaultMaxRangeBytes;
    public int MaxConcurrentReads { get; init; } = DefaultMaxConcurrentReads;
    public long MaxInFlightBytes { get; init; } = DefaultMaxInFlightBytes;

    public static PresentationAssetOptions Default => new();

    public void Validate()
    {
        if (MaxManifestBytes <= 0 || MaxManifestBytes > AbsoluteMaxManifestBytes ||
            MaxAssetBytes <= 0 || MaxAssetBytes > AbsoluteMaxAssetBytes ||
            MaxRangeBytes <= 0 || MaxRangeBytes > MaxAssetBytes ||
            MaxConcurrentReads <= 0 || MaxConcurrentReads > AbsoluteMaxConcurrentReads ||
            MaxInFlightBytes <= 0 || MaxInFlightBytes > AbsoluteMaxInFlightBytes ||
            MaxInFlightBytes < MaxAssetBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(PresentationAssetOptions), "Presentation asset limits are inconsistent.");
        }
    }
}

public sealed class PresentationAssetReadGate
{
    private readonly PresentationAssetOptions options;
    private readonly object sync = new();
    private int activeReads;
    private long activeBytes;

    public PresentationAssetReadGate(PresentationAssetOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.options.Validate();
    }

    public PresentationAssetGateSnapshot Snapshot
    {
        get
        {
            lock (sync)
                return new(activeReads, activeBytes);
        }
    }

    public bool TryAcquire(long bytes, out IDisposable lease)
    {
        lease = NullLease.Instance;
        if (bytes <= 0 || bytes > options.MaxAssetBytes)
            return false;

        lock (sync)
        {
            if (activeReads >= options.MaxConcurrentReads || bytes > options.MaxInFlightBytes - activeBytes)
                return false;
            activeReads++;
            activeBytes += bytes;
            lease = new Lease(this, bytes);
            return true;
        }
    }

    private void Release(long bytes)
    {
        lock (sync)
        {
            if (activeReads <= 0 || activeBytes < bytes)
                return;
            activeReads--;
            activeBytes -= bytes;
        }
    }

    private sealed class Lease(PresentationAssetReadGate owner, long bytes) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                owner.Release(bytes);
        }
    }

    private sealed class NullLease : IDisposable
    {
        public static NullLease Instance { get; } = new();

        public void Dispose() { }
    }
}

public readonly record struct PresentationAssetGateSnapshot(int ActiveReads, long ActiveBytes);
