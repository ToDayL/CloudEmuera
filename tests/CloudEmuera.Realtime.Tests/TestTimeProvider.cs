namespace CloudEmuera.Realtime.Tests;

internal sealed class TestTimeProvider : TimeProvider
{
    private long timestamp;
    private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => timestamp;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration)
    {
        timestamp += duration.Ticks;
        utcNow = utcNow.Add(duration);
    }
}
