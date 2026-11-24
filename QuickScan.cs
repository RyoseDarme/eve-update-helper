using System.Collections.Concurrent;

namespace EveUpdateHelper;

internal static class QuickPlanner
{
    internal const int PoolLimit = 2048;
    internal const int ConfirmationLimit = 128;
    internal const int DownloadLimit = 10;
    internal const int DesiredResults = 3;
    internal const int AliasSeconds = 8;
    internal const int ConnectWaitSeconds = 6;
    internal static bool CanCompareMore(double remaining, bool hasResult) =>
        !hasResult || remaining >= 10 + 2 + ConnectWaitSeconds + 4 * (AliasSeconds + 2 + 12 + 2);
    internal static TcpSample[] Rank(IEnumerable<TcpSample> samples) => samples.Where(n => n.Received > 0 && n.Sent > 0)
        .OrderByDescending(n => (double)n.Received / n.Sent).ThenBy(n => n.AverageMs).ToArray();
    internal static string[] ForConfirmation(IEnumerable<TcpSample> samples, IEnumerable<string> preferred)
    {
        var reachable = Rank(samples);
        var known = preferred.ToHashSet();
        return reachable.Take(ConfirmationLimit).Concat(reachable.Where(n => known.Contains(n.IP)))
            .Select(n => n.IP).Distinct().ToArray();
    }
    internal static string[] VerificationIPs(IEnumerable<NodeResult> downloaded, IEnumerable<string> own, IEnumerable<TcpSample> tcp,
        IEnumerable<string>? current = null) =>
        Rank(tcp.Where(n => (current ?? []).Contains(n.IP))).Take(1).Select(n => n.IP)
            .Concat(downloaded.Where(n => n.Success && NetworkScanner.RankScore(n) > 0).OrderByDescending(NetworkScanner.RankScore).Select(n => n.IP))
            .Concat(own).Concat(Rank(tcp).Select(n => n.IP)).Distinct().Take(4).ToArray();
    internal static TcpSample[] ForDownloads(IEnumerable<TcpSample> samples, IEnumerable<string> current)
    {
        var ranked = Rank(samples);
        var known = current.ToHashSet();
        // Include one current-route baseline within the existing ten-candidate
        // limit so a faster established route is not missed by TCP ordering.
        return ranked.Where(n => known.Contains(n.IP)).Take(1).Concat(ranked).DistinctBy(n => n.IP)
            .Take(DownloadLimit).ToArray();
    }
}

internal sealed partial class NetworkScanner
{
    // The quick path has its own scheduler. Standard and precise keep their
    // broader HTTPS/survey/calibration pipeline.
    async Task<ScanResult> RunQuickAsync(CancellationToken ct)
    {
        var profile = ScanProfile.For(ScanMode.Quick);
        using var budget = new ScanBudget(profile.TotalSeconds, ct);
        var snapshots = new Dictionary<(string Domain, string IP), NodeResult>();
        var tcp = new ConcurrentDictionary<string, TcpSample>();
        var urls = BackupUrls();
        int prefixCount = 0, sampledCount = 0;
        bool expired = false;
        void Publish(NodeResult node)
        {
            if (tcp.TryGetValue(node.IP, out var sample))
                node = node with { TcpLatencyMs = sample.AverageMs, TcpReceived = sample.Received, TcpSent = sample.Sent };
            snapshots[(node.Domain, node.IP)] = node;
            row(node);
        }
        async Task<NodeResult> Download(string domain, string ip, int seconds)
        {
            string source = candidateCache[domain].TryGetValue(ip, out var found) ? found : "CloudFront 快速 TCP 排序候选";
            var node = new NodeResult(domain, ip, source, false, 0, 0, 0, "", "等待实际下载验证", urls[domain]);
            var result = await (hooks?.Download?.Invoke(node, seconds, false, budget.Token) ??
                TestNode(domain, ip, source, node.TestUrl, true, budget.Token, seconds, log, sampleProgress: Publish,
                    compareDecoded: true, connectWaitSeconds: QuickPlanner.ConnectWaitSeconds));
            Publish(result);
            return result;
        }
        log("快速档：参考原程序，TCP 排序后直接下载；最多比较前 10 个候选，凑够 3 个有效资源节点即进入域名确认。");
        log("最多 2048 个网段抽样 IP 加本机候选：先各连 1 次，只对前 128 个及可连接的本机候选复测 4 次。");
        log("主资源沿用你提供的原测速文件；持续 10 秒。其他下载域名各自确认 8 秒，两个 -fallback 仍独立验证。总时限 180 秒。");
        try
        {
            var catalog = hooks?.Catalog != null ? await hooks.Catalog(budget.Token) : CloudFrontCatalog.Embedded();
            prefixCount = catalog.Prefixes.Count;
            var sampled = CloudFrontCatalog.Sample(catalog.Prefixes).Take(QuickPlanner.PoolLimit).ToArray();
            sampledCount = sampled.Length;
            var discoveries = await Task.WhenAll(Domains.Select(async d =>
                (Domain: d, IPs: await (hooks?.Dns?.Invoke(d, budget.Token) ?? Candidates(d, budget.Token)))));
            foreach (var discovery in discoveries) candidateCache[discovery.Domain] = discovery.IPs;
            EveDomains.MergeFamilyCandidates(candidateCache);
            var preferred = candidateCache.Values.SelectMany(p => p.Keys).Distinct().ToArray();
            var pool = preferred.Concat(sampled.Select(p => p.IP)).Distinct().ToArray();
            log($"TCP 粗筛：{pool.Length} 个 IP，200 并发，各连 1 次；保留候选再复测，减少无效等待。");
            using var gate = new SemaphoreSlim(200);
            int completed = 0;
            await Task.WhenAll(pool.Select(async ip =>
            {
                await gate.WaitAsync(budget.Token);
                try
                {
                    tcp[ip] = await (hooks?.Tcp?.Invoke(ip, 1, budget.Token) ??
                        FastScreening.TcpAsync(ip, 1, budget.Token));
                    int count = Interlocked.Increment(ref completed);
                    if (count % 500 == 0) log($"快速 TCP 进度 {count}/{pool.Length}…");
                }
                finally { gate.Release(); }
            }));
            var shortlist = QuickPlanner.ForConfirmation(tcp.Values, preferred);
            log($"TCP 粗筛完成：{tcp.Count} 个已测，{tcp.Values.Count(n => n.Received > 0)} 个可连接；仅复测 {shortlist.Length} 个候选，各 {profile.TcpRepeats} 次。");
            var confirmed = new ConcurrentDictionary<string, TcpSample>();
            await Task.WhenAll(shortlist.Select(async ip =>
            {
                await gate.WaitAsync(budget.Token);
                try
                {
                    var sample = await (hooks?.Tcp?.Invoke(ip, profile.TcpRepeats, budget.Token) ??
                        FastScreening.TcpAsync(ip, profile.TcpRepeats, budget.Token));
                    confirmed[ip] = sample;
                    tcp[ip] = sample;
                }
                finally { gate.Release(); }
            }));
            var ranked = QuickPlanner.Rank(confirmed.Values);
            log($"TCP 候选复测完成：{ranked.Length} 个可连接；按成功率和延迟排序后直接下载，失败和零速度不计入有效节点。");
            var downloaded = new List<NodeResult>();
            var currentResourceIPs = candidateCache[EveDomains.Resources]
                .Where(p => p.Value.Contains("系统") || p.Value.Contains("用户补充")).Select(p => p.Key).ToArray();
            if (currentResourceIPs.Length > 0) log("主资源候选包含当前 hosts / 补充节点基线；与新候选共用原有数量，不额外重复测速。");
            foreach (var candidate in QuickPlanner.ForDownloads(ranked, currentResourceIPs))
            {
                budget.Token.ThrowIfCancellationRequested();
                // Reserve one complete independent measurement for the other
                // four download domains before comparing more resource nodes.
                if (!QuickPlanner.CanCompareMore(budget.RemainingSeconds, downloaded.Count > 0))
                {
                    log("优先保留剩余时间完成其他下载域名验证，停止增加资源候选。");
                    break;
                }
                log($"资源直接测速 resources.eveonline.com → {candidate.IP}；已找到 {downloaded.Count}/{QuickPlanner.DesiredResults} 个有效节点。");
                var result = await Download(EveDomains.Resources, candidate.IP, profile.SampleSeconds);
                if (IsQuickResult(result, EveDomains.Resources)) downloaded.Add(result);
                else log("下载未通过，不计入有效结果：" + result.Detail);
                if (downloaded.Count >= QuickPlanner.DesiredResults) break;
            }
            if (downloaded.Count == 0)
            {
                log("未获得有效资源下载结果，本轮不应用 hosts。可使用标准档扩大搜索。");
            }
            else
            {
                var winner = downloaded.OrderByDescending(RankScore).First();
                log($"资源优胜节点 {winner.IP}，网络持续 {winner.SpeedMBps:F2} MiB/s；开始用真实域名分别确认其他下载。");
                var pending = DownloadDomains.Where(d => d != EveDomains.Resources)
                    .ToDictionary(d => d, d => new Queue<string>(QuickPlanner.VerificationIPs(downloaded, candidateCache[d].Keys, ranked,
                        candidateCache[d].Where(p => p.Value.Contains("系统") || p.Value.Contains("用户补充")).Select(p => p.Key))));
                // Give every required domain one attempt before retrying a failed
                // domain; otherwise fallback timeouts can starve launcher updates.
                while (pending.Values.Any(q => q.Count > 0))
                {
                    foreach (var domain in pending.Keys.ToArray())
                    {
                        var queue = pending[domain];
                        if (queue.Count == 0) continue;
                        var ip = queue.Dequeue();
                        budget.Token.ThrowIfCancellationRequested();
                        if (budget.RemainingSeconds < QuickPlanner.AliasSeconds + 5)
                        {
                            log("剩余时间不足以完成独立确认，保留已通过结果；缺少域名时不可应用。");
                            goto Finish;
                        }
                        log($"独立确认 {domain} → {ip}，持续 {QuickPlanner.AliasSeconds} 秒。");
                        var result = await Download(domain, ip, QuickPlanner.AliasSeconds);
                        if (IsQuickResult(result, domain)) queue.Clear();
                        else log("该域名未通过，先完成其他域名，再尝试下一候选：" + result.Detail);
                    }
                }
                if (budget.RemainingSeconds > 4 && candidateCache[Domains[2]].Count > 0)
                {
                    string domain = Domains[2];
                    var candidates = QuickPlanner.Rank(tcp.Values.Where(t => candidateCache[domain].ContainsKey(t.IP)));
                    if (candidates.Length > 0)
                    {
                        using var legacyTimeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                        legacyTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                        var node = new NodeResult(domain, candidates[0].IP, "旧界面可选检查", false, 0, 0, 0, "", "", urls[domain]);
                        try
                        {
                            Publish(await (hooks?.Download?.Invoke(node, 8, false, legacyTimeout.Token) ??
                                TestNode(domain, node.IP, node.Source, node.TestUrl, false, legacyTimeout.Token)));
                        }
                        catch (OperationCanceledException) when (!budget.Token.IsCancellationRequested) { log("旧界面检查超时，不影响五个文件下载域名。"); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            expired = true;
            log("快速档时限已到，只保留完整通过的结果。");
        }
        Finish:
        ct.ThrowIfCancellationRequested();
        var nodes = snapshots.Values.Select(n => n.Detail.StartsWith("测速中")
            ? n with { Success = false, Detail = "快速验证未完成，不可应用" } : n).ToList();
        var best = new Dictionary<string, NodeResult>();
        foreach (var domain in Domains)
        {
            var winner = domain == Domains[2] ? nodes.Where(n => n.Domain == domain && n.Success).OrderBy(n => n.LatencyMs).FirstOrDefault()
                : nodes.Where(n => IsQuickResult(n, domain)).OrderByDescending(RankScore).FirstOrDefault();
            if (winner != null)
            {
                best[domain] = winner;
                log($"{domain} 推荐 {winner.IP}；{(domain == Domains[2] ? $"响应 {winner.LatencyMs:F0} ms" : $"网络持续 {winner.SpeedMBps:F2} MiB/s")}");
            }
            else log(domain + " 未完成有效验证，不会写入未知结果。");
        }
        var coverage = new ScanCoverage(prefixCount, sampledCount, tcp.Count, tcp.Values.Count(n => n.Received > 0),
            0, 0, 0, 0, nodes.Count(n => DownloadDomains.Contains(n.Domain) && IsQuickResult(n, n.Domain)));
        var scan = new ScanResult(nodes, best, DateTime.Now, expired, budget.ElapsedSeconds, profile.TotalSeconds, profile.SampleSeconds, coverage);
        log($"快速结束，用时 {scan.ElapsedSeconds:F1} 秒；TCP {tcp.Count} 个，持续通过 {coverage.SustainedPassed} 个节点；未做独立 HEAD 广筛。");
        log(CanApply(scan) ? "五个下载域名均已独立验证，可应用。" : "部分域名未通过，不能应用；可选择标准档扩大比较。");
        return scan;
    }

    internal static bool IsQuickResult(NodeResult node, string domain) => node.Domain == domain && node.Success &&
        node.Bytes >= 128 * 1024 && double.IsFinite(node.SpeedMBps) && RankScore(node) > 0 &&
        node.MeasuredSeconds >= node.RequiredSeconds - .5 && node.RequiredSeconds is (8 or 10);
}
