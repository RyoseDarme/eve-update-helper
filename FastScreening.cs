using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace EveUpdateHelper;

internal sealed record TcpSample(string IP, double AverageMs, int Received, int Sent);
internal sealed record SurveySample(NodeResult Node, bool Usable);
internal sealed record HttpsSample(NodeResult Node, bool Usable);

internal static class FastScreening
{
    internal static async Task<TcpSample> TcpAsync(string ip, int repeats, CancellationToken ct)
    {
        if (!NetworkScanner.PublicIPv4(ip)) throw new ArgumentException("Invalid public IP.");
        var elapsed = new List<double>();
        for (int i = 0; i < repeats; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.LingerState = new LingerOption(true, 0); // No application data: release pure-connect probes immediately.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            var clock = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), 443), timeout.Token);
                elapsed.Add(clock.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (SocketException) { }
            catch (OperationCanceledException) { }
        }
        return new TcpSample(ip, elapsed.Count > 0 ? elapsed.Average() : 0, elapsed.Count, repeats);
    }

    internal static async Task<HttpsSample> HttpsAsync(NodeResult candidate, CancellationToken ct, Func<HttpClient>? clientFactory = null)
    {
        var url = new Uri(candidate.TestUrl);
        if (!NetworkScanner.DownloadDomains.Contains(candidate.Domain) || url.Host != candidate.Domain || url.Scheme != "https" ||
            !url.IsDefaultPort || url.UserInfo.Length != 0 || !NetworkScanner.PublicIPv4(candidate.IP)) throw new ArgumentException("Unexpected HTTPS target.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var clock = Stopwatch.StartNew();
        try
        {
            using var client = clientFactory?.Invoke() ?? NetworkScanner.Client(candidate.IP);
            using var request = new HttpRequestMessage(HttpMethod.Head, candidate.TestUrl);
            request.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            NetworkScanner.ValidateFileResponse(response);
            var region = response.Headers.TryGetValues("X-Amz-Cf-Pop", out var pop) ? pop.FirstOrDefault() ?? "" : "";
            return new(candidate with { Success = false, Bytes = 0, SpeedMBps = 0, SmallFileMBps = 0, MeasuredSeconds = 0, RequiredSeconds = 0,
                CalibrationRounds = 0, QualityScore = 0, ParallelSpeedMBps = 0, DecodedSpeedMBps = 0,
                LatencyMs = clock.Elapsed.TotalMilliseconds, Region = region,
                Detail = "HTTPS 文件头通过，下载未测；不可应用" }, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(candidate with { Success = false, Detail = "HTTPS 筛查失败：" + NetworkScanner.ErrorText(ex) }, false); }
    }

    internal static async Task<SurveySample> DownloadAsync(NodeResult candidate, CancellationToken ct,
        Func<HttpClient>? clientFactory = null)
    {
        var url = new Uri(candidate.TestUrl);
        if (!NetworkScanner.DownloadDomains.Contains(candidate.Domain) || url.Host != candidate.Domain ||
            url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 || !NetworkScanner.PublicIPv4(candidate.IP))
            throw new ArgumentException("Unexpected survey target.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        long bytes = 0;
        var transfer = new Stopwatch();
        var clock = Stopwatch.StartNew();
        string region = "";
        double responseMs = 0;
        try
        {
            using var client = clientFactory?.Invoke() ?? NetworkScanner.Client(candidate.IP);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 64 * 1024 * 1024 - 1);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            NetworkScanner.ValidateFileResponse(response);
            responseMs = clock.Elapsed.TotalMilliseconds;
            if (response.Headers.TryGetValues("X-Amz-Cf-Pop", out var pops)) region = pops.FirstOrDefault() ?? "";
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[32 * 1024];
            transfer.Start();
            try
            {
                while (bytes < 64 * 1024 * 1024 && transfer.Elapsed.TotalSeconds < 2)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 64 * 1024 * 1024 - bytes)), timeout.Token);
                    if (n == 0) break;
                    bytes += n;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && bytes >= 64 * 1024) { }
            bool usable = bytes >= 64 * 1024;
            var seconds = transfer.Elapsed.TotalSeconds;
            return new SurveySample(candidate with
            {
                Success = false, Bytes = bytes, SpeedMBps = 0, InitialSpeedMBps = bytes / Math.Max(seconds, .001) / 1024 / 1024,
                LatencyMs = responseMs, Region = region, MeasuredSeconds = seconds, RequiredSeconds = 0,
                Detail = usable ? "下载初测完成，尚需持续复测；不可直接应用" : "下载初测数据不足；不可应用"
            }, usable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new SurveySample(candidate with
            {
                Success = false, Bytes = bytes, SpeedMBps = 0, LatencyMs = responseMs,
                Detail = "下载初测失败：" + NetworkScanner.ErrorText(ex), RequiredSeconds = 0
            }, false);
        }
    }

    internal static NodeResult[] Rank(IEnumerable<SurveySample> survey) => survey.Where(s => s.Usable)
        .OrderByDescending(s => s.Node.InitialSpeedMBps).ThenBy(s => s.Node.TcpLatencyMs).Select(s => s.Node).ToArray();
}
