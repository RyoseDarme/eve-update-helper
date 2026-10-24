using System.Collections.Concurrent;

namespace EveUpdateHelper;

internal static class QuickPlanner
{
    internal const int PoolLimit = 2048;
    internal const int DownloadLimit = 10;
    internal const int DesiredResults = 3;
    internal const int AliasSeconds = 8;
    internal static TcpSample[] Rank(IEnumerable<TcpSample> samples) => samples.Where(n => n.Received > 0 && n.Sent > 0)
        .OrderByDescending(n => (double)n.Received / n.Sent).ThenBy(n => n.AverageMs).ToArray();
    internal static string[] VerificationIPs(IEnumerable<NodeResult> downloaded, IEnumerable<string> own, IEnumerable<TcpSample> tcp) =>
        downloaded.Where(n => n.Success && NetworkScanner.RankScore(n) > 0).OrderByDescending(NetworkScanner.RankScore).Select(n => n.IP)
            .Concat(own).Concat(Rank(tcp).Select(n => n.IP)).Distinct().Take(4).ToArray();
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
                TestNode(domain, ip, source, node.TestUrl, true, budget.Token, seconds, log, sampleProgress: Publish, compareDecoded: true));
            Publish(result);
            return result;
        }
        log("快速档：参考原程序，TCP 排序后直接下载；最多比较前 10 个候选，凑够 3 个有效资源节点即进入域名确认。");
        log("采用随包 CloudFront 网段的最多 2048 个抽样 IP，加本机 DNS / 补充节点；无在线网段更新、独立 HEAD 广筛或资源清单下载。");
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
            var preferred = candidateCache.Values.SelectMany(p => p.Keys).Distinct();
            var pool = preferred.Concat(sampled.Select(p => p.IP)).Distinct().ToArray();
            log($"TCP 测速：{pool.Length} 个 IP，200 并发，各测 {profile.TcpRepeats} 次，按连接成功率和平均延迟排序。");
            using var gate = new SemaphoreSlim(200);
            int completed = 0;
            await Task.WhenAll(pool.Select(async ip =>
            {
                await gate.WaitAsync(budget.Token);
                try
                {
                    tcp[ip] = await (hooks?.Tcp?.Invoke(ip, profile.TcpRepeats, budget.Token) ??
                        FastScreening.TcpAsync(ip, profile.TcpRepeats, budget.Token));
                    int count = Interlocked.Increment(ref completed);
                    if (count % 500 == 0) log($"快速 TCP 进度 {count}/{pool.Length}…");
                }
                finally { gate.Release(); }
            }));
            var ranked = QuickPlanner.Rank(tcp.Values);
            log($"TCP 完成：{tcp.Count} 个已测，{ranked.Length} 个可连接；开始直接下载，失败和零速度不计入有效节点。");
            var downloaded = new List<NodeResult>();
            foreach (var candidate in ranked.Take(QuickPlanner.DownloadLimit))
            {
                budget.Token.ThrowIfCancellationRequested();
                // Reserve one complete independent measurement for the other
                // four download domains before comparing more resource nodes.
                double reserve = (DownloadDomains.Length - 1) * (QuickPlanner.AliasSeconds + 4);
                if (downloaded.Count > 0 && budget.RemainingSeconds < profile.SampleSeconds + 5 + reserve)
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
                foreach (var domain in DownloadDomains.Where(d => d != EveDomains.Resources))
                {
                    foreach (var ip in QuickPlanner.VerificationIPs(downloaded, candidateCache[domain].Keys, ranked))
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        if (budget.RemainingSeconds < QuickPlanner.AliasSeconds + 5)
                        {
                            log("剩余时间不足以完成独立确认，保留已通过结果；缺少域名时不可应用。");
                            goto Finish;
                        }
                        log($"独立确认 {domain} → {ip}，持续 {QuickPlanner.AliasSeconds} 秒。");
                        var result = await Download(domain, ip, QuickPlanner.AliasSeconds);
                        if (IsQuickResult(result, domain)) break;
                        log("该域名未通过，尝试下一候选：" + result.Detail);
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
