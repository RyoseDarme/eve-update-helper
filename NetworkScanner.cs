using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EveUpdateHelper;

public sealed record NodeResult(string Domain, string IP, string Source, bool Success,
    double LatencyMs, double SpeedMBps, long Bytes, string Region, string Detail, string TestUrl,
    double MeasuredSeconds = 0, double SmallFileMBps = 0, int RequestCount = 0, string[]? AdditionalUrls = null, int RequiredSeconds = 20,
    double TcpLatencyMs = 0, int TcpReceived = 0, int TcpSent = 0, double InitialSpeedMBps = 0,
    int CalibrationRounds = 0, double[]? CalibrationSpeeds = null, double QualityScore = 0, double ParallelSpeedMBps = 0,
    double DecodedSpeedMBps = 0, string ContentEncoding = "", int ParallelRounds = 0);
public sealed record ScanResult(List<NodeResult> Nodes, Dictionary<string, NodeResult> Best, DateTime TestedAt,
    bool BudgetExpired = false, double ElapsedSeconds = 0, int TotalSeconds = 120, int SampleSeconds = 20, ScanCoverage? Coverage = null);
public sealed record ScanCoverage(int CdnPrefixes, int SampledIPs, int TcpProbed, int TcpReachable,
    int HttpsPlanned, int HttpsCompleted, int HttpsPassed, int DownloadSurveyed, int SustainedPassed);

internal sealed partial class NetworkScanner(Action<string> log, Action<NodeResult> row,
    Func<Uri, int, CancellationToken, Task<string>>? metadataReader = null, int totalBudgetSeconds = 120, ScanMode? mode = null,
    IReadOnlyDictionary<string, string[]>? additionalCandidates = null, ScanHooks? hooks = null)
{
    readonly Dictionary<string, Dictionary<string, string>> candidateCache = new();
    readonly Dictionary<string, List<string>> extraFiles = new();
    internal static readonly string[] Domains = EveDomains.All;
    internal static readonly string[] DownloadDomains = EveDomains.Downloads;
    const string HistoricalResource = "https://resources.eveonline.com/d2/d218fb784d254413_60f784f44054fcc251a3afe5ac301c3f";
    const string VerifiedBinary = "https://binaries.eveonline.com/ba/bad1c0acadc3e43d_f9e8bcab719e249ba8bd50414d42143f";
    internal static HttpClient Client(string? ip = null, bool decompressMetadata = false, Action<string>? connected = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            AutomaticDecompression = decompressMetadata
                ? DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                : DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }
        };
        if (ip != null || connected != null)
        {
            var address = ip != null ? IPAddress.Parse(ip) : null;
            handler.ConnectCallback = async (context, ct) =>
            {
                var socket = address != null ? new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) : new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                try
                {
                    if (address != null) await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    else await socket.ConnectAsync(context.DnsEndPoint, ct);
                    var remote = ((IPEndPoint)socket.RemoteEndPoint!).Address;
                    connected?.Invoke((remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString());
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        }
        // Keeping the original URL preserves TLS SNI, certificate validation and HTTP Host.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EveUpdateHelper/" + AppInfo.Version);
        return client;
    }

    async Task<string> GetText(Uri url, int maxBytes, CancellationToken ct)
    {
        if (metadataReader != null) return await metadataReader(url, maxBytes, ct);
        var choices = new List<string?> { null };
        if (candidateCache.TryGetValue(url.Host, out var candidates))
            choices.AddRange(candidates.OrderByDescending(p => p.Value.Contains("阿里") || p.Value.Contains("腾讯")).Select(p => p.Key).Take(2));
        log($"读取 {url.AbsolutePath}：同时尝试正常解析和国内 DNS 节点…");
        return await FirstSuccessfulRead(choices, async (ip, token) =>
        {
            try { return await GetTextFromIP(url, maxBytes, ip, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log($"清单节点 {ip ?? "正常解析"} 不可用：{ErrorText(ex)}"); throw; }
        }, ct);
    }

    internal static async Task<string> FirstSuccessfulRead(IEnumerable<string?> choices,
        Func<string?, CancellationToken, Task<string>> read, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = choices.Select(ip => read(ip, stop.Token)).ToList();
        var all = tasks.ToArray();
        try
        {
            while (tasks.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(tasks);
                tasks.Remove(completed);
                try { return await completed; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
            throw new IOException("所有清单读取节点均失败。");
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(all); } catch { }
        }
    }

    async Task<string> GetTextFromIP(Uri url, int maxBytes, string? ip, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = Client(ip, decompressMetadata: true);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (output.Length < maxBytes)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - output.Length)), timeout.Token);
            if (n == 0) break;
            output.Write(buffer, 0, n);
        }
        if (output.Length >= maxBytes) throw new IOException("清单超出大小限制，未使用不完整的清单。");
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    internal async Task<Dictionary<string, string>> DiscoverUrls(CancellationToken ct, int metadataSeconds = 20)
    {
        var urls = BackupUrls();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(metadataSeconds));
        try { await DiscoverCurrentUrls(urls, deadline.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log($"当前文件清单读取未完成：{ErrorText(ex)}；继续用已验证的备用 EVE 文件测速。");
        }
        // A CDN alias may route differently: reuse only the file path, never a
        // measured IP or success result. Every host receives its own HTTPS tests.
        foreach (var family in EveDomains.Families)
        {
            urls[family[1]] = EveDomains.Rehost(urls[family[0]], family[1]);
            if (extraFiles.TryGetValue(family[0], out var siblings))
                extraFiles[family[1]] = siblings.Select(u => EveDomains.Rehost(u, family[1])).ToList();
        }
        var actual = LauncherLogInspector.ReadRecent();
        foreach (var domain in DownloadDomains)
        {
            if (!extraFiles.ContainsKey(domain)) extraFiles[domain] = [];
            extraFiles[domain].InsertRange(0, actual.Files[domain].TakeLast(2));
            extraFiles[domain] = extraFiles[domain].Where(u => u != urls[domain]).Distinct().Take(2).ToList();
            if (actual.Files[domain].Count > 0) log($"从本机启动器日志找到 {domain} 实际文件请求，加入小请求验证。");
        }
        log("清单阶段结束，开始验证节点；备用文件也必须实际下载成功才允许应用。");
        return urls;
    }

    internal static Dictionary<string, string> BackupUrls() => new()
    {
        [Domains[0]] = HistoricalResource,
        [Domains[1]] = VerifiedBinary,
        [Domains[2]] = "https://launcher.eveonline.com/version.json",
        [Domains[3]] = "https://launcher.ccpgames.com/eve-online/release/win32/x64/eve-online-latest+Setup.exe",
        [EveDomains.ResourceFallback] = EveDomains.Rehost(HistoricalResource, EveDomains.ResourceFallback),
        [EveDomains.BinaryFallback] = EveDomains.Rehost(VerifiedBinary, EveDomains.BinaryFallback)
    };

    async Task DiscoverCurrentUrls(Dictionary<string, string> urls, CancellationToken ct)
    {
        log("读取 EVE 当前版本和文件清单…");
        async Task<string> ReadBinary(string path, int limit)
        {
            return await FirstSuccessfulRead([EveDomains.BinaryFallback, EveDomains.Binaries],
                (domain, token) => GetText(new Uri("https://" + domain + "/" + path), limit, token), ct);
        }
        var versionText = await ReadBinary("eveclient_TQ.json", 64 * 1024);
        using var version = JsonDocument.Parse(versionText);
        var build = version.RootElement.GetProperty("build").ToString();
        if (!Regex.IsMatch(build, @"^\d{1,12}$")) throw new IOException("EVE 版本号格式异常。");
        var manifest = await ReadBinary($"eveonline_{build}.txt", 2 * 1024 * 1024);
        var files = ParseManifest(manifest);
        var binary = files.Where(f => f.Size >= 1024 * 1024).OrderByDescending(f => f.Size).FirstOrDefault()
            ?? throw new IOException("当前版本清单没有可测速的文件。");
        urls[Domains[1]] = "https://" + Domains[1] + "/" + binary.Path;
        extraFiles[Domains[1]] = files.Where(f => f.Size >= 128 * 1024 && f.Path != binary.Path).Take(2)
            .Select(f => "https://" + Domains[1] + "/" + f.Path).ToList();
        var index = files.FirstOrDefault(f => f.Name.Equals("app:/resfileindex.txt", StringComparison.OrdinalIgnoreCase));
        if (index != null)
        {
            try
            {
                var resourceManifest = await ReadBinary(index.Path, 32 * 1024 * 1024);
                var resource = ParseManifest(resourceManifest).Where(f => f.Size >= 4 * 1024 * 1024)
                    .OrderByDescending(f => f.Size).FirstOrDefault();
                if (resource != null)
                {
                    urls[Domains[0]] = "https://" + Domains[0] + "/" + resource.Path;
                    extraFiles[Domains[0]] = ParseManifest(resourceManifest).Where(f => f.Size >= 128 * 1024 && f.Path != resource.Path)
                        .Take(2).Select(f => "https://" + Domains[0] + "/" + f.Path).ToList();
                    log($"从当前资源清单选择：{resource.Name}，文件约 {resource.Size / 1024 / 1024}MB。");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log("资源清单读取失败，尝试教程中的真实资源文件：" + ErrorText(ex)); }
        }
        if (urls[Domains[0]] == HistoricalResource)
        {
            urls[Domains[0]] = HistoricalResource;
            log("未找到当前资源测速文件，使用教程中的备用资源；仍须通过实际下载验证。");
        }
        log($"当前版本 {build}；资源与二进制的普通 / -fallback 域名分别使用真实文件测速。");
    }

    internal sealed record ManifestFile(string Name, string Path, long Size);
    internal static List<ManifestFile> ParseManifest(string text)
    {
        var list = new List<ManifestFile>();
        foreach (var line in text.Split('\n'))
        {
            var values = line.Trim().Split(',');
            if (values.Length < 5 || !Regex.IsMatch(values[1], @"^[a-fA-F0-9]{2}/[a-fA-F0-9_]+$") ||
                !long.TryParse(values[4], out var size) || size <= 0) continue;
            list.Add(new ManifestFile(values[0], values[1], size));
        }
        return list;
    }

    internal static bool CanApply(ScanResult? scan) => scan != null && DownloadDomains.All(d =>
        scan.Best.TryGetValue(d, out var node) && node.Domain == d && node.Success &&
        node.Bytes >= 128 * 1024 && double.IsFinite(node.SpeedMBps) && node.SpeedMBps > 0 && node.SmallFileMBps > 0 &&
        node.RequiredSeconds is (8 or 10 or 12 or 15 or 20 or 30) && node.MeasuredSeconds >= node.RequiredSeconds - .5 &&
        Uri.TryCreate(node.TestUrl, UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host == d &&
        url.IsDefaultPort && url.UserInfo.Length == 0 && PublicIPv4(node.IP));

    async Task<Dictionary<string, string>> Candidates(string domain, CancellationToken ct)
    {
        var ips = new Dictionary<string, string>();
        async Task<(string Source, List<string> IPs)> SystemQuery()
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain, ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
                return ("系统 DNS / 当前 hosts", addresses.Select(a => a.ToString()).ToList());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log($"{domain} 系统 DNS：{ErrorText(ex)}"); return ("系统 DNS", []); }
        }
        async Task<(string Source, List<string> IPs)> PublicQuery(string name, string template)
        {
            try
            {
                using var client = Client();
                client.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await client.GetAsync(string.Format(template, domain), timeout.Token);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var addresses = new List<string>();
                if (json.RootElement.TryGetProperty("Answer", out var answers))
                    foreach (var answer in answers.EnumerateArray())
                        if (answer.GetProperty("type").GetInt32() == 1) addresses.Add(answer.GetProperty("data").GetString()!);
                return (name, addresses);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log($"{name} 暂不可用：{ErrorText(ex)}；继续测试其他来源。"); return (name, []); }
        }
        async Task<(string Source, List<string> IPs)> WireQuery(string name, string server)
        {
            try { return (name, (await DnsWire.Resolve(domain, server, ct)).ToList()); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { log($"{name}：{ErrorText(ex)}，继续其他解析来源。"); return (name, []); }
        }
        var answers = await Task.WhenAll(SystemQuery(),
            PublicQuery("阿里 DNS", "https://dns.alidns.com/resolve?name={0}&type=A"),
            PublicQuery("腾讯 DNS", "https://doh.pub/dns-query?name={0}&type=A"),
            WireQuery("阿里 UDP 223.5.5.5", "223.5.5.5"), WireQuery("阿里 UDP 223.6.6.6", "223.6.6.6"),
            WireQuery("腾讯 UDP 119.29.29.29", "119.29.29.29"), WireQuery("腾讯 UDP 119.28.28.28", "119.28.28.28"));
        foreach (var answer in answers)
            foreach (var ip in answer.IPs.Where(PublicIPv4))
                ips[ip] = ips.TryGetValue(ip, out var existing) ? existing + " / " + answer.Source : answer.Source;
        if (additionalCandidates != null && additionalCandidates.TryGetValue(domain, out var supplied))
            foreach (var ip in supplied.Where(PublicIPv4))
                ips[ip] = ips.TryGetValue(ip, out var source) ? source + " / 用户补充" : "用户补充";
        return ips;
    }

    internal static bool PublicIPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != value) return false;
        var b = ip.GetAddressBytes();
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
            !(b[0] == 198 && b[1] is 18 or 19) && !(b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) &&
            !(b[0] == 198 && b[1] == 51 && b[2] == 100) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    internal static async Task<NodeResult> TestNode(string domain, string ip, string source,
        string url, bool download, CancellationToken ct, int seconds = 20, Action<string>? progress = null, string[]? additionalUrls = null,
        Func<HttpClient>? clientFactory = null, Func<double>? testElapsedSeconds = null, Action<NodeResult>? sampleProgress = null,
        bool varyRange = false, bool compareDecoded = false)
    {
        if (!Domains.Contains(domain) || !PublicIPv4(ip)) throw new ArgumentException("不允许的测速域名或 IP。");
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Host != domain || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new ArgumentException("测速地址必须是对应 EVE 域名的 HTTPS 文件。");
        if (seconds is < 3 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        double latency = 0;
        string region = "";
        var meter = new TransferMeter(2);
        var decodedMeter = new TransferMeter(2);
        string contentEncoding = "";
        var transfer = new Stopwatch();
        double Elapsed() => testElapsedSeconds?.Invoke() ?? transfer.Elapsed.TotalSeconds;
        int requests = 0;
        try
        {
            using var client = clientFactory?.Invoke() ?? Client(ip);
            if (domain == Domains[2]) return await CheckLauncher(client, ip, source, ct);
            if (!download)
            {
                using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headers.CancelAfter(TimeSpan.FromSeconds(10));
                var clock = Stopwatch.StartNew();
                using var head = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, headers.Token);
                ValidateFileResponse(response);
                latency = clock.Elapsed.TotalMilliseconds;
                if (response.Headers.TryGetValues("X-Amz-Cf-Pop", out var pops)) region = pops.FirstOrDefault() ?? "";
                return new NodeResult(domain, ip, source, true, latency, 0, 0, region, "HTTPS 已验证，等待持续下载测速", url);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            int nextProgress = 5;
            double nextRow = 1;
            long totalLength = 0;
            long maxBytes = SampleByteLimit(seconds);
            const long rangeBytes = 128L * 1024 * 1024;
            try
            {
                while (meter.MeasuredSeconds(Elapsed()) < seconds && meter.Bytes < maxBytes)
                {
                    long offset = totalLength > 2 * rangeBytes ? Random.Shared.NextInt64(0, totalLength - rangeBytes) : 0;
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    if (varyRange) request.Headers.Range = new RangeHeaderValue(offset, offset + rangeBytes - 1);
                    var requestClock = Stopwatch.StartNew();
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    ValidateFileResponse(response);
                    if (requests == 0)
                    {
                        latency = requestClock.Elapsed.TotalMilliseconds;
                        if (response.Headers.TryGetValues("X-Amz-Cf-Pop", out var pops)) region = pops.FirstOrDefault() ?? "";
                        transfer.Start();
                        timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 2));
                    }
                    totalLength = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? 0;
                    if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != offset)
                        throw new IOException("服务器返回的文件区间与请求不一致。");
                    contentEncoding = string.Join(",", response.Content.Headers.ContentEncoding);
                    await using var raw = await response.Content.ReadAsStreamAsync(timeout.Token);
                    long thisRequest = 0;
                    using var counted = new CountingReadStream(raw, n => { thisRequest += n; meter.Observe(Elapsed(), n); });
                    bool decode = compareDecoded && !varyRange && contentEncoding.Equals("gzip", StringComparison.OrdinalIgnoreCase);
                    using Stream stream = decode ? new System.IO.Compression.GZipStream(counted, System.IO.Compression.CompressionMode.Decompress, true) : counted;
                    var buffer = new byte[64 * 1024];
                    requests++;
                    while ((!varyRange || thisRequest < rangeBytes) && meter.Bytes < maxBytes && meter.MeasuredSeconds(Elapsed()) < seconds)
                    {
                        int n = await stream.ReadAsync(buffer, timeout.Token);
                        if (n == 0) break;
                        if (decode) decodedMeter.Observe(Elapsed(), n);
                        if (Elapsed() >= nextRow)
                        {
                            sampleProgress?.Invoke(new NodeResult(domain, ip, source, false, latency, meter.Speed(Elapsed()), meter.Bytes, region,
                                Elapsed() < 2 ? "测速中：预热" : $"测速中：{meter.MeasuredSeconds(Elapsed()):F0}/{seconds} 秒", url,
                                meter.MeasuredSeconds(Elapsed()), 0, requests, additionalUrls, seconds,
                                DecodedSpeedMBps: decodedMeter.Speed(Elapsed()), ContentEncoding: contentEncoding));
                            nextRow = Elapsed() + 1;
                        }
                        if (Elapsed() >= nextProgress)
                        {
                            progress?.Invoke($"{domain} → {ip}：已测 {meter.MeasuredSeconds(Elapsed()):F0}/{seconds} 秒，持续 {meter.Speed(Elapsed()):F2} MiB/s");
                            nextProgress += 5;
                        }
                    }
                    if (thisRequest == 0) throw new IOException("服务器没有返回文件数据。");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && meter.Sufficient(Elapsed(), seconds)) { }
            transfer.Stop();
            bool success = meter.Sufficient(Elapsed(), seconds);
            double stableSeconds = meter.MeasuredSeconds(Elapsed());
            double stableSpeed = meter.Speed(Elapsed());
            if (!success) return new NodeResult(domain, ip, source, false, latency, meter.Speed(Elapsed()), meter.Bytes,
                region, meter.Bytes >= maxBytes ? "达到样本上限，但时长不足；未作为持续测速结果" : "持续测速时长或数据不足", url,
                meter.MeasuredSeconds(Elapsed()), 0, requests);
            double smallSpeed = 0;
            string smallNote = "";
            progress?.Invoke($"{domain} → {ip}：持续下载完成，正在验证小请求…");
            try
            {
                smallSpeed = await SmallRequests(client, uri, totalLength, ct, additionalUrls);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { smallNote = "；小请求失败：" + ErrorText(ex); }
            return new NodeResult(domain, ip, source, smallSpeed > 0, latency, stableSpeed, meter.Bytes, region,
                $"持续 {stableSeconds:F0}秒；小请求 {smallSpeed:F2} MiB/s{smallNote}" +
                    (decodedMeter.Bytes > 0 ? $"；解压读取 {decodedMeter.Speed(Elapsed()):F2} MiB/s（不是网络下载速度）" : contentEncoding == "gzip" ? "；按 gzip 压缩字节计速" : ""), url,
                stableSeconds, smallSpeed, requests, additionalUrls, seconds,
                DecodedSpeedMBps: decodedMeter.Bytes > 0 ? decodedMeter.Speed(Elapsed()) : 0, ContentEncoding: contentEncoding);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new NodeResult(domain, ip, source, false, latency, 0, meter.Bytes, region, ErrorText(ex), url); }
    }

    internal static void ValidateFileResponse(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            throw new IOException($"HTTP {(int)response.StatusCode}，未使用此节点");
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("html") || type.Contains("json") || type.StartsWith("text/"))
            throw new IOException("返回文本或错误页面，未使用此节点");
    }

    static async Task<double> SmallRequests(HttpClient client, Uri uri, long length, CancellationToken ct, string[]? additionalUrls)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var clock = Stopwatch.StartNew();
        long bytes = 0;
        for (int i = 0; i < 3; i++)
        {
            long offset = length > 256 * 1024 ? Random.Shared.NextInt64(0, length - 128 * 1024) : 0;
            var chosen = additionalUrls != null && additionalUrls.Length > 0 && i < additionalUrls.Length ? new Uri(additionalUrls[i]) : uri;
            if (chosen.Scheme != "https" || chosen.Host != uri.Host || !chosen.IsDefaultPort || chosen.UserInfo.Length > 0)
                throw new IOException("额外测试文件地址异常。");
            if (chosen != uri) offset = 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, chosen);
            request.Headers.Range = new RangeHeaderValue(offset, offset + 128 * 1024 - 1);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            ValidateFileResponse(response);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[32 * 1024];
            long received = 0;
            while (received < 128 * 1024)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 128 * 1024 - received)), timeout.Token);
                if (n == 0) break;
                received += n;
            }
            if (received == 0) throw new IOException("小请求无数据。");
            bytes += received;
        }
        return bytes / Math.Max(clock.Elapsed.TotalSeconds, .001) / 1024 / 1024;
    }

    internal static async Task<NodeResult> CheckLauncher(HttpClient client, string ip, string source, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        using var response = await client.GetAsync("https://launcher.eveonline.com/version.json", timeout.Token);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        using var version = JsonDocument.Parse(text);
        if (!version.RootElement.TryGetProperty("release", out var release) || release.ValueKind != JsonValueKind.String)
            throw new IOException("启动器版本清单格式异常。");
        string region = response.Headers.TryGetValues("X-Amz-Cf-Pop", out var pops) ? pops.FirstOrDefault() ?? "" : "";
        return new NodeResult(Domains[2], ip, source, true, clock.Elapsed.TotalMilliseconds, 0, text.Length, region,
            "启动器版本清单已验证（此域名按响应时间选择）", "https://launcher.eveonline.com/version.json");
    }

    internal static string ErrorText(Exception ex) => ex switch
    {
        OperationCanceledException => "连接或下载超时",
        AuthenticationException => "HTTPS 证书或 TLS 握手失败",
        HttpRequestException when ex.InnerException is AuthenticationException => "HTTPS 证书或 TLS 握手失败",
        _ => ex.GetBaseException().Message
    };

    internal async Task<ScanResult> RunAsync(CancellationToken ct)
    {
        if (mode == ScanMode.Quick) return await RunQuickAsync(ct);
        var profile = mode.HasValue ? ScanProfile.For(mode.Value) : null;
        using var budget = new ScanBudget(profile?.TotalSeconds ?? totalBudgetSeconds, ct);
        int sampleSeconds = profile?.SampleSeconds ?? budget.SampleSeconds;
        bool precise = mode == ScanMode.Precise;
        bool quick = mode == ScanMode.Quick;
        double Remaining() => precise ? double.PositiveInfinity : budget.RemainingSeconds;
        var snapshots = new ConcurrentDictionary<(string Domain, string IP), NodeResult>();
        var tcpResults = new ConcurrentDictionary<string, TcpSample>();
        int prefixCount = 0, sampledCount = 0, httpPlanned = 0, httpCompleted = 0, httpPassedCount = 0, surveyCount = 0;
        void Publish(NodeResult node)
        {
            if (tcpResults.TryGetValue(node.IP, out var tcp))
                node = node with { TcpLatencyMs = tcp.AverageMs, TcpReceived = tcp.Received, TcpSent = tcp.Sent };
            if (snapshots.TryGetValue((node.Domain, node.IP), out var previous) && node.InitialSpeedMBps == 0)
                node = node with { InitialSpeedMBps = previous.InitialSpeedMBps };
            snapshots[(node.Domain, node.IP)] = node; row(node);
        }
        Task<Dictionary<string, string>> Discover(string domain) => hooks?.Dns?.Invoke(domain, budget.Token) ?? Candidates(domain, budget.Token);
        Task<TcpSample> Connect(string ip, int count) => hooks?.Tcp?.Invoke(ip, count, budget.Token) ?? FastScreening.TcpAsync(ip, count, budget.Token);
        Task Pause(TimeSpan duration) => hooks?.Delay?.Invoke(duration, budget.Token) ?? Task.Delay(duration, budget.Token);
        Task<NodeResult> Measure(NodeResult node, int seconds, bool varyRange = false) => hooks?.Download?.Invoke(node, seconds, varyRange, budget.Token) ??
            TestNode(node.Domain, node.IP, node.Source, node.TestUrl, true, budget.Token, seconds, log,
                node.AdditionalUrls ?? (extraFiles.TryGetValue(node.Domain, out var files) ? files.ToArray() : null), sampleProgress: Publish,
                varyRange: varyRange, compareDecoded: quick);
        log($"模式 {profile?.Name ?? "限时"}；{(precise ? "不设总时限，完整覆盖并重复验证" : $"总时限 {budget.TotalSeconds} 秒")}；单节点持续测 {sampleSeconds} 秒（另有预热）。");
        bool expired = false;
        try
        {
            log("并行收集六个域名的系统、阿里和腾讯 DNS 节点（包含新版优先使用的两个 -fallback 域名）…");
            var catalogTask = hooks?.Catalog?.Invoke(budget.Token) ?? CloudFrontCatalog.Load(log, budget.Token);
            var discoveries = await Task.WhenAll(Domains.Select(async domain =>
                (Domain: domain, IPs: await Discover(domain))));
            foreach (var discovery in discoveries) candidateCache[discovery.Domain] = discovery.IPs;
            if (precise)
                for (int pass = 1; pass < 3; pass++)
                {
                    log($"精校准：第 {pass + 1}/3 次收集 DNS，合并轮换节点。");
                    await Pause(TimeSpan.FromSeconds(1));
                    var extra = await Task.WhenAll(Domains.Select(async d => (Domain: d, IPs: await Discover(d))));
                    foreach (var found in extra) foreach (var pair in found.IPs) candidateCache[found.Domain].TryAdd(pair.Key, pair.Value);
                }
            // Include the user's existing family mappings even when DNS for the
            // other alias returns only slow nodes. They are candidates, not winners.
            EveDomains.MergeFamilyCandidates(candidateCache);
            var metadataTask = DiscoverUrls(budget.Token, profile?.MetadataSeconds ?? budget.MetadataSeconds);
            var preferredIPs = candidateCache.Values.SelectMany(c => c.Keys).Distinct().ToArray();
            var catalog = await catalogTask;
            var sampled = CloudFrontCatalog.Sample(catalog.Prefixes);
            prefixCount = catalog.Prefixes.Count; sampledCount = sampled.Count;
            log($"候选来源：{catalog.Source}，日期 {catalog.Date}，{catalog.Prefixes.Count} 条 CloudFront 网段 → {sampled.Count} 个 /24 抽样 IP；另外保留 {preferredIPs.Length} 个 DNS / 补充节点。");
            var uniqueIPs = preferredIPs.Concat(sampled.Select(s => s.IP)).Distinct().ToArray();
            int tcpRepeats = profile?.TcpRepeats ?? (budget.TotalSeconds >= 180 ? 4 : 2);
            log($"TCP 全池初探：{uniqueIPs.Length} 个 IP，200 并发、1 秒连接超时；先各探一次，再对 HTTPS 候选复核 {tcpRepeats} 次。");
            using var tcpGate = new SemaphoreSlim(200);
            int tcpCompleted = 0;
            var tcpTasks = uniqueIPs.Select(async ip =>
            {
                await tcpGate.WaitAsync(budget.Token);
                try
                {
                    var sample = await Connect(ip, 1);
                    tcpResults[ip] = sample;
                    int completed = Interlocked.Increment(ref tcpCompleted);
                    if (completed % 2000 == 0) log($"TCP 初探进度 {completed}/{uniqueIPs.Length}…");
                }
                finally { tcpGate.Release(); }
            }).ToArray();
            await Task.WhenAll(metadataTask, Task.WhenAll(tcpTasks));
            var urls = await metadataTask;
            int poolLimit = quick ? 96 : precise ? 512 : 192;
            var httpIPs = CandidatePlanner.ForHttps(tcpResults.Values, preferredIPs, poolLimit);
            log($"TCP 初探完成：{tcpCompleted} 个已测，{tcpResults.Values.Count(t => t.Received > 0)} 个可连接；HTTPS 候选选取 {httpIPs.Length} 个，保留 DNS / 用户节点，并兼顾低延迟和不同网段。");
            await Task.WhenAll(httpIPs.Select(async ip =>
            {
                await tcpGate.WaitAsync(budget.Token);
                try { tcpResults[ip] = await Connect(ip, tcpRepeats); }
                finally { tcpGate.Release(); }
            }));
            var sampledByIP = sampled.ToDictionary(s => s.IP);
            foreach (var domain in DownloadDomains)
                foreach (var ip in httpIPs)
                    if (!candidateCache[domain].ContainsKey(ip)) candidateCache[domain][ip] = sampledByIP.TryGetValue(ip, out var location)
                        ? $"AWS CloudFront {location.Prefix} / {location.Region}" : "其他 EVE 域名的 DNS 节点，需本域独立验证";
            foreach (var domain in Domains)
                foreach (var candidate in candidateCache[domain])
                {
                    var tcp = tcpResults[candidate.Key];
                    Publish(new NodeResult(domain, candidate.Key, candidate.Value, false, 0, 0, 0, "",
                        tcp.Received > 0 ? $"TCP 可连接 {tcp.Received}/{tcp.Sent}，尚未验证文件" : $"TCP 连接失败 {tcp.Received}/{tcp.Sent}", urls[domain]));
                }
            log("开始按真实域名做 HTTPS 筛查，检查状态码、证书和文件类型，并记录实际边缘地区。TCP 通不等于能下载。");

            var httpsPassed = new ConcurrentDictionary<(string Domain, string IP), NodeResult>();
            using var httpDeadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            if (!precise) httpDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Math.Min(quick ? 45 : 90, Remaining() - (sampleSeconds + 5) * DownloadDomains.Length - 20))));
            using var httpGate = new SemaphoreSlim(48);
            var httpTasks = snapshots.Values.Where(n => DownloadDomains.Contains(n.Domain) && n.TcpReceived > 0).Select(async node =>
            {
                try
                {
                    await httpGate.WaitAsync(httpDeadline.Token);
                    try
                    {
                        var checkedNode = await (hooks?.Https?.Invoke(node, httpDeadline.Token) ?? FastScreening.HttpsAsync(node, httpDeadline.Token));
                        Interlocked.Increment(ref httpCompleted);
                        Publish(checkedNode.Node);
                        if (checkedNode.Usable) { httpsPassed[(node.Domain, node.IP)] = checkedNode.Node; Interlocked.Increment(ref httpPassedCount); }
                    }
                    finally { httpGate.Release(); }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }).ToArray();
            httpPlanned = httpTasks.Length;
            await Task.WhenAll(httpTasks);
            budget.Token.ThrowIfCancellationRequested();
            log("HTTPS 通过：" + string.Join("，", DownloadDomains.Select(d => $"{d}: {httpsPassed.Values.Count(n => n.Domain == d)}")));

            using var legacyDeadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            legacyDeadline.CancelAfter(TimeSpan.FromSeconds(8));
            using var legacyGate = new SemaphoreSlim(6);
            var legacyTasks = snapshots.Values.Where(n => n.Domain == Domains[2] && n.TcpReceived > 0).Select(async node =>
            {
                await legacyGate.WaitAsync(legacyDeadline.Token);
                try { Publish(await (hooks?.Download?.Invoke(node, sampleSeconds, false, legacyDeadline.Token) ?? TestNode(node.Domain, node.IP, node.Source, node.TestUrl, false, legacyDeadline.Token))); }
                finally { legacyGate.Release(); }
            }).ToArray();
            var survey = DownloadDomains.ToDictionary(d => d, _ => new List<SurveySample>());
            var candidates = DownloadDomains.ToDictionary(d => d, d => CandidatePlanner.AcrossRegions(httpsPassed.Values.Where(n => n.Domain == d)));
            using var surveyDeadline = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            int surveySeconds = profile?.SurveySeconds ?? budget.SurveySeconds;
            if (surveySeconds > 0) surveyDeadline.CancelAfter(TimeSpan.FromSeconds(surveySeconds));
            log(quick ? "快速档：HTTP 校验后跨地区轮流直接下载测速，按实际下载选择；每域名先测最多 3 个节点。" : $"逐个下载初筛：预算 {surveySeconds} 秒；按实际边缘地区轮流覆盖，短测只供排序，不能直接应用。");
            try
            {
                int surveyRounds = quick ? 0 : candidates.Values.Max(c => c.Length);
                for (int round = 0; round < surveyRounds; round++)
                    foreach (var domain in DownloadDomains)
                    {
                        if (round >= candidates[domain].Length) continue;
                        var candidate = candidates[domain][round];
                        log($"下载初测 {domain} → {candidate.IP}，候选 {round + 1}/{candidates[domain].Length}");
                        var initial = await (hooks?.Survey?.Invoke(candidate, surveyDeadline.Token) ?? FastScreening.DownloadAsync(candidate, surveyDeadline.Token));
                        surveyCount++;
                        survey[domain].Add(initial); Publish(initial.Node);
                    }
            }
            catch (OperationCanceledException) when (!budget.Token.IsCancellationRequested)
            { log("初筛阶段预算已到，开始复测已获得真实下载数据的优胜节点。"); }
            finally
            {
                if (!quick && !precise) legacyDeadline.Cancel();
                try { await Task.WhenAll(legacyTasks); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            log("初测覆盖：" + string.Join("，", DownloadDomains.Select(d => $"{d}: {survey[d].Count}/{candidates[d].Length}")));
            var queues = DownloadDomains.ToDictionary(domain => domain, domain => quick ? CandidatePlanner.Quick(candidates[domain]) : CandidatePlanner.Verification(survey[domain], candidates[domain], precise));
            log("持续测试先保留当前系统 / 用户补充节点作基线，再与扩大候选池中的节点比较。");
            int verificationRounds = queues.Values.Max(q => q.Length);
            for (int round = 0; round < verificationRounds; round++)
            {
                foreach (var domain in DownloadDomains)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    if (round >= queues[domain].Length) continue;
                    if (Remaining() < sampleSeconds + 5)
                    {
                        log("剩余时间不足以开始一次完整持续测试，保留已验证结果。");
                        goto Finish;
                    }
                    var candidate = queues[domain][round];
                    log($"持续测量第 {round + 1} 轮：{domain} → {candidate.IP}，{(precise ? "完整候选验证" : $"剩余 {Remaining():F0} 秒")}");
                    var result = await Measure(candidate, sampleSeconds);
                    if (!result.Success) log($"{domain} → {candidate.IP} 下载验证未通过：{result.Detail}");
                    Publish(result);
                }
            }
            if (precise)
            {
                foreach (var domain in DownloadDomains)
                {
                    var finalists = snapshots.Values.Where(n => n.Domain == domain && n.Success && RankScore(n) > 0)
                        .OrderByDescending(RankScore).Take(3).ToArray();
                    foreach (var candidate in finalists)
                    {
                        var rounds = new List<NodeResult> { candidate };
                        for (int pass = 1; pass <= 2; pass++)
                        {
                            log($"精校准复测 {pass + 1}/3：{domain} → {candidate.IP}，持续 30 秒；比较不同时段、区间与小文件。");
                            await Pause(TimeSpan.FromSeconds(2));
                            rounds.Add(await Measure(candidate, 30, varyRange: pass == 2));
                        }
                        var calibrated = Calibration.Combine(candidate, rounds);
                        if (calibrated.Success)
                        {
                            log($"三路并行对照：{domain} → {candidate.IP}；该总速单独展示，不冒充单连接速度。");
                            var parallelRounds = new List<ParallelSample>();
                            for (int pass = 1; pass <= 2; pass++)
                            {
                                log($"三路共同计时 {pass}/2：{domain} → {candidate.IP}，每轮持续 20 秒。");
                                parallelRounds.Add(await (hooks?.Parallel?.Invoke(candidate, budget.Token) ?? ParallelDownload.Measure(candidate, 20, budget.Token)));
                            }
                            bool parallelPassed = parallelRounds.All(n => n.Success);
                            double parallelSpeed = parallelPassed ? parallelRounds.Min(n => n.SpeedMBps) : 0;
                            calibrated = calibrated with { ParallelSpeedMBps = parallelSpeed,
                                ParallelRounds = parallelRounds.Count(n => n.Success),
                                QualityScore = parallelPassed ? .6 * calibrated.QualityScore + .4 * parallelSpeed : calibrated.QualityScore,
                                Detail = calibrated.Detail + (parallelPassed ? $"；三路2轮取低值 {parallelSpeed:F2} MiB/s" : "；三路复测未齐，按单连接稳定性推荐") };
                        }
                        Publish(calibrated);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            expired = true;
            log("所选总时限已到，已停止网络测试，只保留已完成验证的结果。");
        }
        Finish:
        ct.ThrowIfCancellationRequested();
        var results = snapshots.Values.OrderBy(n => Array.IndexOf(Domains, n.Domain)).ThenBy(n => n.LatencyMs).ToList();
        for (int i = 0; i < results.Count; i++)
            if (results[i].Detail.StartsWith("测速中"))
            {
                results[i] = results[i] with { Success = false, Detail = "下载验证未完成（时限已到），不可应用" };
                row(results[i]);
            }
            else if (results[i].Success && DownloadDomains.Contains(results[i].Domain) && results[i].Bytes == 0)
            {
                results[i] = results[i] with { Success = false, Detail = "仅 HTTPS 预检完成，下载未测；不可应用" };
                row(results[i]);
            }
        var best = new Dictionary<string, NodeResult>();
        foreach (var domain in Domains)
        {
            var winner = domain == Domains[2]
                ? results.Where(n => n.Domain == domain && n.Success).OrderBy(n => n.LatencyMs).FirstOrDefault()
                : results.Where(n => n.Domain == domain && n.Success && (!precise || n.CalibrationRounds == 3) && n.MeasuredSeconds >= n.RequiredSeconds - .5 && RankScore(n) > 0)
                    .OrderByDescending(RankScore).FirstOrDefault();
            if (winner != null)
            {
                best[domain] = winner;
                log(domain == Domains[2] ? $"{domain} 推荐 {winner.IP}，延迟 {winner.LatencyMs:F0}ms" :
                    $"{domain} 推荐 {winner.IP}，持续 {winner.SpeedMBps:F2} MiB/s，小请求 {winner.SmallFileMBps:F2} MiB/s");
            }
            else log($"{domain} 未完成有效验证；不会应用未知或短测结果。");
        }
        log($"测速流程结束，实际用时 {budget.ElapsedSeconds:F1}/{budget.TotalSeconds} 秒。");
        var coverage = new ScanCoverage(prefixCount, sampledCount, tcpResults.Count, tcpResults.Values.Count(t => t.Received > 0),
            httpPlanned, httpCompleted, httpPassedCount, surveyCount, results.Count(n => DownloadDomains.Contains(n.Domain) && n.Success && n.SpeedMBps > 0));
        log($"覆盖报告：网段 {coverage.CdnPrefixes}；抽样 {coverage.SampledIPs}；TCP 已测 {coverage.TcpProbed}；HTTPS 完成 {coverage.HttpsCompleted}/{coverage.HttpsPlanned}、通过 {coverage.HttpsPassed}；下载初测 {coverage.DownloadSurveyed}；持续通过 {coverage.SustainedPassed}。");
        return new ScanResult(results, best, DateTime.Now, expired, budget.ElapsedSeconds, budget.TotalSeconds, sampleSeconds, coverage);
    }

    internal static long SampleByteLimit(int seconds) => Math.Max(2L * 1024 * 1024 * 1024, (seconds + 2L) * 128 * 1024 * 1024);
    internal static double RankScore(NodeResult node) => node.Success && node.SpeedMBps > 0 && node.SmallFileMBps > 0
        ? node.QualityScore > 0 ? node.QualityScore : node.SpeedMBps : 0;
}
