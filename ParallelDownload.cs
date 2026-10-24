using System.Diagnostics;

namespace EveUpdateHelper;

internal sealed record ParallelSample(bool Success, double SpeedMBps, double MeasuredSeconds, long Bytes, string Detail);

internal static class ParallelDownload
{
    internal static async Task<ParallelSample> Measure(NodeResult node, int seconds, CancellationToken ct,
        Func<HttpClient>? clientFactory = null, Func<double>? elapsedSeconds = null)
    {
        var uri = new Uri(node.TestUrl);
        if (!NetworkScanner.DownloadDomains.Contains(node.Domain) || uri.Host != node.Domain || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !NetworkScanner.PublicIPv4(node.IP)) throw new ArgumentException("Unexpected parallel target.");
        if (seconds is < 3 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Stopwatch();
        double Elapsed() => elapsedSeconds?.Invoke() ?? clock.Elapsed.TotalSeconds;
        int connected = 0;
        long total = 0, scored = 0;
        long[] received = new long[3];
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task Download(int index)
        {
            try
            {
                using var client = clientFactory?.Invoke() ?? NetworkScanner.Client(node.IP);
                bool first = true;
                while (first || Elapsed() < seconds + 2)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    NetworkScanner.ValidateFileResponse(response);
                    if (first)
                    {
                        first = false;
                        if (Interlocked.Increment(ref connected) == 3)
                        {
                            clock.Start(); deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 2)); ready.TrySetResult();
                        }
                        await ready.Task.WaitAsync(deadline.Token);
                    }
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                    var buffer = new byte[64 * 1024];
                    long current = 0;
                    while (Elapsed() < seconds + 2 && Interlocked.Read(ref total) < 3 * NetworkScanner.SampleByteLimit(seconds))
                    {
                        int count = await stream.ReadAsync(buffer, deadline.Token);
                        if (count == 0) break;
                        received[index] += count; current += count;
                        Interlocked.Add(ref total, count);
                        if (Elapsed() >= 2) Interlocked.Add(ref scored, count);
                        if (current % (1024 * 1024) == 0) await Task.Yield();
                    }
                    if (current == 0 && Elapsed() < seconds + 2) throw new IOException("多连接下载无数据。");
                    if (Interlocked.Read(ref total) >= 3 * NetworkScanner.SampleByteLimit(seconds)) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && Elapsed() >= seconds + 1.5) { }
            catch (Exception ex) { errors.Enqueue(NetworkScanner.ErrorText(ex)); deadline.Cancel(); }
        }
        await Task.WhenAll(Enumerable.Range(0, 3).Select(Download));
        clock.Stop(); ct.ThrowIfCancellationRequested();
        double measured = Math.Max(0, Elapsed() - 2);
        bool success = errors.IsEmpty && measured >= seconds - .5 && received.All(n => n >= 128 * 1024) && scored >= 128 * 1024;
        return new ParallelSample(success, success ? scored / Math.Max(.001, measured) / 1024 / 1024 : 0, measured, total,
            success ? "三条连接使用同一时间窗口，排除共同预热后按总字节计算" : "三路验证不足：" + string.Join("；", errors));
    }
}
