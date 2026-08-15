namespace CloudEmuera.Api;

/// <summary>
/// Exposes a bounded view over an already-positioned read stream.  The
/// presentation asset endpoint uses this for single-byte-range responses so
/// the HTTP result cannot copy bytes beyond the requested interval.
/// </summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream inner;
    private readonly long length;
    private long remaining;
    private long position;

    public BoundedReadStream(Stream inner, long length)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        this.inner = inner;
        this.length = length;
        remaining = length;
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)offset > (uint)buffer.Length || count < 0 || buffer.Length - offset < count)
            throw new ArgumentOutOfRangeException(nameof(count));
        int requested = (int)Math.Min(count, remaining);
        if (requested == 0) return 0;
        int read = inner.Read(buffer, offset, requested);
        Advance(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int requested = (int)Math.Min(buffer.Length, remaining);
        if (requested == 0) return 0;
        int read = inner.Read(buffer[..requested]);
        Advance(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int requested = (int)Math.Min(buffer.Length, remaining);
        if (requested == 0) return 0;
        int read = await inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
        Advance(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)offset > (uint)buffer.Length || count < 0 || buffer.Length - offset < count)
            throw new ArgumentOutOfRangeException(nameof(count));
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new NotSupportedException());

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void Advance(int count)
    {
        position += count;
        remaining -= count;
    }
}
