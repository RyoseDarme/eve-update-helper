namespace EveUpdateHelper;

// Count bytes before decompression. The caller owns the underlying stream.
internal sealed class CountingReadStream(Stream inner, Action<int> observe) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); observe(n); return n; }
    public override int Read(Span<byte> buffer) { int n = inner.Read(buffer); observe(n); return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    { int n = await inner.ReadAsync(buffer, ct); observe(n); return n; }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
