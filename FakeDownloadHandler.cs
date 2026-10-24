using System.Net;
using System.Net.Http.Headers;

namespace EveUpdateHelper;

// Deterministic regression fixture: a finite 16MB response completes before the test duration.
internal sealed class FakeDownloadHandler(Action advanceClock, bool html = false) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Method == HttpMethod.Head)
        {
            var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            head.Content.Headers.ContentType = new MediaTypeHeaderValue(html ? "text/html" : "application/octet-stream");
            head.Content.Headers.ContentLength = 1024L * 1024 * 1024;
            return Task.FromResult(head);
        }
        var range = request.Headers.Range?.Ranges.Single();
        long from = range?.From ?? 0;
        long length = range?.To - from + 1 ?? 16L * 1024 * 1024;
        var response = new HttpResponseMessage(range != null ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(new GeneratedStream(length, advanceClock)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(html ? "text/html" : "application/octet-stream");
        if (range != null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, from + length - 1, 1024L * 1024 * 1024);
        response.Content.Headers.ContentLength = length;
        return Task.FromResult(response);
    }

    sealed class GeneratedStream(long length, Action advance) : Stream
    {
        long position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, length - position);
            if (n == 0) return 0;
            buffer[..n].Clear();
            position += n;
            advance();
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
