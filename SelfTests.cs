using System.Text;

namespace EveUpdateHelper;

internal static class SelfTests
{
    internal static string Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EveHelperTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var report = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            report.Add("PASS: " + description);
        }
        UpdateTests.Run(Check);
        var addresses = new Dictionary<string, string>
        {
            [NetworkScanner.Domains[0]] = "18.65.14.45",
            [NetworkScanner.Domains[1]] = "18.244.18.123",
            [NetworkScanner.Domains[2]] = "18.65.14.34",
            [NetworkScanner.Domains[3]] = "18.65.14.92",
            [EveDomains.ResourceFallback] = "13.225.117.114",
            [EveDomains.BinaryFallback] = "3.170.229.35"
        };
        try
        {
            string original = "# keep ! 1.2.3.4\r\n\r\n127.0.0.1 localhost\r\n1.2.3.4 resources.eveonline.com other.example # comment !\r\n1.2.3.40 untouched.example\r\n5.6.7.8 BINARIES.EVEONLINE.COM # binary\r\n";
            var bytes = Encoding.UTF8.GetBytes(original);
            var output = HostsManager.Rewrite(bytes, addresses);
            var text = Encoding.UTF8.GetString(output);
            Check(text.Contains("# keep ! 1.2.3.4\r\n\r\n127.0.0.1 localhost"), "comments, blank lines, localhost preserved");
            Check(text.Contains("1.2.3.4 other.example # comment !"), "mixed unrelated hostname retains old IP");
            Check(text.Contains("1.2.3.40 untouched.example"), "IP prefix not replaced");
            Check(text.Contains("# binary"), "inline comment preserved");
            Check(text.Contains("18.65.14.45 resources.eveonline.com") && text.Contains("18.244.18.123 binaries.eveonline.com"), "domains get independent addresses");
            Check(output.SequenceEqual(HostsManager.Rewrite(output, addresses)), "repeat application is idempotent");
            var empty = HostsManager.Rewrite([], addresses);
            Check(Encoding.UTF8.GetString(empty).Split('\n').Count(l => l.Contains("eveonline.com") || l.Contains("ccpgames.com")) == 6, "empty hosts gets all six independently verified domains");
            Check(text.Contains("18.65.14.92 launcher.ccpgames.com"), "current official launcher download receives its own address");
            var noLegacy = new Dictionary<string, string>(addresses);
            noLegacy.Remove(NetworkScanner.Domains[2]);
            var retainedLegacy = Encoding.UTF8.GetString(HostsManager.Rewrite(Encoding.UTF8.GetBytes("1.2.3.4 launcher.eveonline.com\r\n"), noLegacy));
            Check(retainedLegacy.Contains("1.2.3.4 launcher.eveonline.com"), "untested optional legacy mapping is preserved");
            Check(text.Contains("18.65.14.34 launcher.eveonline.com"), "launcher domain receives its own address");
            foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.Latin1 })
            {
                var sample = "# 中文 café !\n127.0.0.1 localhost";
                var encoded = encoding.GetPreamble().Concat(encoding.GetBytes(sample)).ToArray();
                var rewritten = HostsManager.Rewrite(encoded, addresses);
                Check(rewritten.AsSpan(0, encoded.Length).SequenceEqual(encoded), "original bytes preserved: " + encoding.WebName);
                var decoded = encoding.GetString(rewritten.AsSpan(encoding.GetPreamble().Length));
                Check(!decoded.Contains('\r'), "LF style retained: " + encoding.WebName);
            }
            bool rejected = false;
            try { HostsManager.Rewrite(Encoding.UTF8.GetBytes("# BEGIN EVE UPDATE HELPER\nkeep.example"), addresses); }
            catch (IOException) { rejected = true; }
            Check(rejected, "broken managed block rejected without writing");
            foreach (var ip in new[] { "127.0.0.1", "10.0.0.1", "192.168.1.1", "::1", "1.2.3.999", "224.1.2.3", "100.64.0.1", "0.0.0.0", "198.18.0.1", "198.19.255.1", "192.0.2.1", "198.51.100.1", "203.0.113.1" })
                Check(!NetworkScanner.PublicIPv4(ip), "invalid or private IP rejected: " + ip);
            Check(NetworkScanner.PublicIPv4("18.65.14.45"), "public IPv4 accepted");
            var snapshot = CloudFrontCatalog.Embedded();
            var globalSample = CloudFrontCatalog.Sample(snapshot.Prefixes, new Random(42));
            Check(snapshot.Prefixes.Count > 100 && globalSample.Count > 10000 && globalSample.All(a => NetworkScanner.PublicIPv4(a.IP)),
                "bundled official CloudFront catalog yields over ten thousand public candidate IPs");
            var fallbackCatalog = CloudFrontCatalog.Load(_ => { }, CancellationToken.None,
                () => new HttpClient(new FakeDownloadHandler(() => { }, html: true))).GetAwaiter().GetResult();
            Check(fallbackCatalog.Source == "随包官方快照" && fallbackCatalog.Prefixes.Count == snapshot.Prefixes.Count,
                "unavailable or invalid online catalog falls back to bundled official CloudFront prefixes");
            var onlyCloudFront = CloudFrontCatalog.Parse("{\"createDate\":\"fixture\",\"prefixes\":[{\"service\":\"AMAZON\",\"ip_prefix\":\"1.2.3.0/24\",\"region\":\"GLOBAL\"},{\"service\":\"CLOUDFRONT\",\"ip_prefix\":\"18.65.0.0/24\",\"region\":\"GLOBAL\"}]}", "fixture");
            Check(onlyCloudFront.Prefixes.Count == 1 && onlyCloudFront.Prefixes[0].Cidr == "18.65.0.0/24",
                "catalog excludes unrelated AWS service address ranges");
            var smallSample = CloudFrontCatalog.Sample([new("18.65.0.0/23", "GLOBAL"), new("18.65.0.0/24", "ap-northeast-1"), new("65.8.76.123/32", "GLOBAL")], new Random(42));
            Check(smallSample.Count == 3 && smallSample.Any(a => a.IP == "65.8.76.123") && smallSample.Count(a => a.IP.StartsWith("18.65.0.")) == 1,
                "CIDR sampling deduplicates overlapping /24 blocks and preserves explicit /32 nodes");
            Check(smallSample.Single(a => a.IP.StartsWith("18.65.0.")).Region == "ap-northeast-1", "more specific regional provenance survives overlap");
            rejected = false;
            try { CloudFrontCatalog.Sample([new("18.0.0.0/8", "GLOBAL")]); } catch (IOException) { rejected = true; }
            Check(rejected, "abnormally large CIDR input is bounded rather than generating millions of probes");
            var tcpFixture = Enumerable.Range(1, 60).Select(i => new TcpSample("18.65.0." + i, i, 4, 4))
                .Concat([new("65.8.76.123", 90, 4, 4), new("3.170.229.33", 200, 4, 4), new("13.1.1.1", 0, 0, 4)]);
            var chosenHttp = CandidatePlanner.ForHttps(tcpFixture, ["3.170.229.33"], 12);
            Check(chosenHttp.Length == 12 && chosenHttp.Contains("3.170.229.33") && chosenHttp.Contains("65.8.76.123") && !chosenHttp.Contains("13.1.1.1"),
                "HTTPS shortlist preserves DNS nodes and network diversity instead of only one fast subnet");
            Check(CandidatePlanner.ForHttps([], [], 12).Length == 0, "empty TCP results finish without shortlist failure");
            using (var quickBudget = new ScanBudget(60, CancellationToken.None))
            using (var standardBudget = new ScanBudget(120, CancellationToken.None))
            using (var detailedBudget = new ScanBudget(300, CancellationToken.None))
            {
                Check(quickBudget.SampleSeconds == 8 && standardBudget.SampleSeconds == 10 && detailedBudget.SampleSeconds == 20,
                    "sampling duration scales with the whole-scan time selection");
                Check(!standardBudget.CanStartSample(14) && standardBudget.CanStartSample(15),
                    "scheduler does not start a sample without sufficient remaining budget");
                Check(standardBudget.RemainingSeconds <= 119 && detailedBudget.SurveySeconds > standardBudget.SurveySeconds,
                    "cleanup margin is reserved and five-minute mode can compare more candidates");
            }
            var meter = new TransferMeter(2);
            meter.Observe(1, 10 * 1024 * 1024);
            Check(meter.ScoredBytes == 0 && meter.Speed(1) == 0, "warm-up burst is excluded from reported throughput");
            meter.Observe(22, 20 * 1024 * 1024);
            Check(Math.Abs(meter.Speed(22) - 1) < .0001 && meter.MeasuredSeconds(22) == 20,
                "reported throughput covers full stable measurement duration");
            Check(meter.Sufficient(22, 20) && !meter.Sufficient(3, 20), "sub-second burst cannot pass a twenty-second test");
            double simulatedSeconds = 0;
            int progressUpdates = 0;
            var finiteDownload = NetworkScanner.TestNode(NetworkScanner.Domains[0], "18.65.14.45", "test", NetworkScanner.BackupUrls()[NetworkScanner.Domains[0]],
                true, CancellationToken.None, 20, clientFactory: () => new HttpClient(new FakeDownloadHandler(() => simulatedSeconds += .01)),
                testElapsedSeconds: () => simulatedSeconds, sampleProgress: n => { if (n.LatencyMs > 0 && n.SpeedMBps > 0) progressUpdates++; }).GetAwaiter().GetResult();
            Check(finiteDownload.Success && finiteDownload.MeasuredSeconds >= 20 && finiteDownload.RequestCount > 1 && finiteDownload.Bytes > 16 * 1024 * 1024,
                "fast finite 16MB responses are repeated until sustained measurement completes");
            Check(Math.Abs(finiteDownload.SpeedMBps - 6.25) < .05, "sustained speed remains correct across multiple completed responses");
            Check(progressUpdates >= 10, "live table receives latency and speed updates during download, before completion");
            var htmlResult = NetworkScanner.TestNode(NetworkScanner.Domains[0], "18.65.14.45", "test", NetworkScanner.BackupUrls()[NetworkScanner.Domains[0]],
                true, CancellationToken.None, clientFactory: () => new HttpClient(new FakeDownloadHandler(() => { }, html: true)))
                .GetAwaiter().GetResult();
            Check(!htmlResult.Success && htmlResult.Bytes == 0, "HTTP 200 error HTML is not accepted as a download result");
            var example = new NodeResult(NetworkScanner.Domains[0], "18.65.14.45", "test", true, 100, 30, 1024 * 1024,
                "HKG", "test", NetworkScanner.BackupUrls()[NetworkScanner.Domains[0]], 20, .2);
            var headSample = FastScreening.HttpsAsync(example, CancellationToken.None,
                () => new HttpClient(new FakeDownloadHandler(() => { }))).GetAwaiter().GetResult();
            Check(headSample.Usable && !headSample.Node.Success && headSample.Node.Bytes == 0, "HTTPing success is kept separate from verified download success");
            var badHead = FastScreening.HttpsAsync(example, CancellationToken.None,
                () => new HttpClient(new FakeDownloadHandler(() => { }, html: true))).GetAwaiter().GetResult();
            Check(!badHead.Usable, "HTTPing rejects text error pages even when status is 200");
            var regionOrder = CandidatePlanner.AcrossRegions([example with { Region = "SIN3-P1", LatencyMs = 10 },
                example with { IP = "18.65.14.46", Region = "SIN3-P2", LatencyMs = 20 },
                example with { IP = "18.65.14.47", Region = "NRT20-P1", LatencyMs = 30 }]);
            Check(regionOrder[1].Region.StartsWith("NRT"), "download exploration covers another edge region before repeating the same region");
            var baselineNode = example with { Source = "系统 DNS / 当前 hosts", Region = "SIN3-P1", LatencyMs = 300 };
            var quickWithBaseline = CandidatePlanner.Quick(regionOrder.Append(baselineNode with { IP = "65.8.76.123" }));
            Check(quickWithBaseline[0].IP == "65.8.76.123" && quickWithBaseline.Any(n => n.Region.StartsWith("NRT")),
                "quick mode measures current DNS as a baseline and still explores other regions");
            var hooks = new ScanHooks
            {
                Catalog = _ => Task.FromResult(new CdnCatalog([new("65.8.76.0/24", "GLOBAL"), new("13.225.158.0/24", "GLOBAL")], "fixture", "fixture")),
                Dns = (_, _) => Task.FromResult(new Dictionary<string, string> { ["18.65.14.45"] = "DNS fixture" }),
                Tcp = (ip, repeats, _) => Task.FromResult(new TcpSample(ip, ip == "18.65.14.45" ? 1 : 100, repeats, repeats)),
                Https = (n, _) => Task.FromResult(new HttpsSample(n with { Region = n.IP.StartsWith("65.") ? "SIN3-P1" : "NRT20-P1", LatencyMs = 50 }, true)),
                Survey = (n, _) => Task.FromResult(new SurveySample(n with { InitialSpeedMBps = n.IP == "18.65.14.45" ? 5 : 80 }, true)),
                Download = (n, seconds, _, _) => Task.FromResult(n with { Success = true, Bytes = 1024 * 1024,
                    SpeedMBps = n.Domain == NetworkScanner.Domains[2] ? 0 : n.IP == "18.65.14.45" ? 5 : 80,
                    LatencyMs = 50, SmallFileMBps = 1, MeasuredSeconds = seconds, RequiredSeconds = seconds, Detail = "fixture complete" }),
                Parallel = (_, _) => Task.FromResult(new ParallelSample(true, 100, 20, 1024 * 1024, "fixture")),
                Delay = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            };
            var calibrationPipeline = new NetworkScanner(_ => { }, _ => { }, metadataReader: (_, _, _) => Task.FromException<string>(new IOException("offline fixture")),
                mode: ScanMode.Precise, hooks: hooks).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(NetworkScanner.CanApply(calibrationPipeline) && NetworkScanner.DownloadDomains.All(d => calibrationPipeline.Best[d].IP != "18.65.14.45"),
                "complete precision pipeline finds faster CloudFront nodes absent from DNS instead of choosing lowest-latency DNS result");
            Check(NetworkScanner.DownloadDomains.All(d => calibrationPipeline.Best[d].CalibrationRounds == 3 && calibrationPipeline.Best[d].ParallelRounds == 2),
                "precision pipeline completes all three single-connection rounds and two parallel windows before recommending finalists");
            Check(calibrationPipeline.Coverage is { SampledIPs: 2, TcpProbed: 3, HttpsPassed: 15, SustainedPassed: 15 },
                "coverage report distinguishes sampled IPs from per-domain validation counts");
            Check(NetworkScanner.RankScore(example with { SpeedMBps = 25, SmallFileMBps = .5 }) < NetworkScanner.RankScore(example),
                "small-request rate cannot overwhelm faster validated sustained downloads");
            double highSpeedClock = 0;
            var highSpeed = NetworkScanner.TestNode(example.Domain, example.IP, "test", example.TestUrl, true, CancellationToken.None, 30,
                clientFactory: () => new HttpClient(new FakeDownloadHandler(() => highSpeedClock += .0008)),
                testElapsedSeconds: () => highSpeedClock).GetAwaiter().GetResult();
            Check(highSpeed.Success && highSpeed.Bytes > 2L * 1024 * 1024 * 1024 && highSpeed.MeasuredSeconds >= 30 && highSpeed.SpeedMBps > 70,
                "70+ MiB/s connection completes thirty-second sample beyond the old 2GB ceiling");
            Check(ScanProfile.For(ScanMode.Quick).SurveySeconds == 0 && ScanProfile.For(ScanMode.Quick).TotalSeconds == 180 &&
                ScanProfile.For(ScanMode.Standard).TotalSeconds == 480 && ScanProfile.For(ScanMode.Precise).TotalSeconds == 0,
                "quick has three-minute budget, standard retains eight minutes, precise has no global deadline");
            var quickOrder = QuickPlanner.Rank([new("18.65.14.45", 1, 1, 4), new("65.8.76.123", 50, 4, 4), new("3.170.229.35", 20, 4, 4), new("13.1.1.1", 0, 0, 4)]);
            Check(quickOrder.Select(n => n.IP).SequenceEqual(new[] { "3.170.229.35", "65.8.76.123", "18.65.14.45" }),
                "quick ranks by TCP success ratio then latency and excludes unreachable addresses");
            var quickCalls = new List<(string Domain, string IP, int Seconds)>();
            ScanResult RunQuickFixture(bool failFallback = false, bool zeroFirst = false, bool largePool = false)
            {
                quickCalls.Clear();
                var fastHooks = new ScanHooks
                {
                    Catalog = _ => Task.FromResult(new CdnCatalog([new(largePool ? "65.0.0.0/12" : "65.8.0.0/20", "GLOBAL")], "fixture", "fixture")),
                    Dns = hooks.Dns, Tcp = hooks.Tcp,
                    Https = (_, _) => throw new Exception("Quick must not run the HEAD survey"),
                    Survey = (_, _) => throw new Exception("Quick must not run short survey downloads"),
                    Parallel = (_, _) => throw new Exception("Quick must not run parallel calibration"),
                    Download = (n, seconds, _, _) =>
                    {
                        quickCalls.Add((n.Domain, n.IP, seconds));
                        bool fail = failFallback && n.Domain == EveDomains.BinaryFallback || zeroFirst && n.Domain == EveDomains.Resources && n.IP == "18.65.14.45";
                        return Task.FromResult(n with { Success = !fail, Bytes = fail ? 0 : 1024 * 1024,
                            SpeedMBps = fail || n.Domain == NetworkScanner.Domains[2] ? 0 : n.IP == "18.65.14.45" ? 5 : 80,
                            SmallFileMBps = fail ? 0 : 1, LatencyMs = 50, MeasuredSeconds = seconds, RequiredSeconds = seconds, Detail = fail ? "fixture failure" : "fixture complete" });
                    }
                };
                return new NetworkScanner(_ => { }, _ => { }, metadataReader: (_, _, _) => throw new Exception("Quick must not download metadata"),
                    mode: ScanMode.Quick, hooks: fastHooks).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            var quickPipeline = RunQuickFixture();
            Check(NetworkScanner.CanApply(quickPipeline) && quickPipeline.Coverage is { TcpProbed: 17, HttpsPlanned: 0, DownloadSurveyed: 0 },
                "complete quick pipeline skips metadata, separate HTTPS survey and calibration while producing independently applicable domains");
            Check(quickCalls.Count(n => n.Domain == EveDomains.Resources) == 3 && quickCalls.Count < QuickPlanner.DownloadLimit,
                "quick ends resource comparison after three valid results instead of exhausting every download candidate");
            Check(NetworkScanner.DownloadDomains.Where(d => d != EveDomains.Resources).All(d => quickCalls.Count(n => n.Domain == d && n.Seconds == 8) == 1),
                "quick independently downloads every other required hostname for eight seconds without copying a resource success");
            Check(NetworkScanner.DownloadDomains.All(d => quickPipeline.Best[d].Domain == d && new Uri(quickPipeline.Best[d].TestUrl).Host == d),
                "quick winners preserve actual individual TLS hostnames and URLs");
            var quickFailedAlias = RunQuickFixture(failFallback: true);
            Check(!NetworkScanner.CanApply(quickFailedAlias) && !quickFailedAlias.Best.ContainsKey(EveDomains.BinaryFallback) &&
                quickCalls.Count(n => n.Domain == EveDomains.BinaryFallback) > 1,
                "failed quick fallback verification retries candidates and blocks hosts application if all fail");
            var quickZero = RunQuickFixture(zeroFirst: true);
            Check(quickCalls.Count(n => n.Domain == EveDomains.Resources) == 4 &&
                !quickZero.Best.Values.Any(n => n.Bytes == 0 && NetworkScanner.DownloadDomains.Contains(n.Domain)),
                "zero-speed and failed downloads do not count toward three valid quick nodes");
            var quickLimited = RunQuickFixture(largePool: true);
            Check(quickLimited.Coverage is { SampledIPs: 2048, TcpProbed: 2049 } && NetworkScanner.CanApply(quickLimited),
                "quick bounds the sampled CDN pool while retaining the independently supplied DNS candidate");
            using (var quickStop = new CancellationTokenSource())
            {
                var stopHooks = new ScanHooks
                {
                    Catalog = hooks.Catalog, Dns = hooks.Dns, Tcp = hooks.Tcp,
                    Download = (_, _, _, token) => { quickStop.Cancel(); return Task.FromCanceled<NodeResult>(token); }
                };
                rejected = false;
                try { new NetworkScanner(_ => { }, _ => { }, mode: ScanMode.Quick, hooks: stopHooks).RunAsync(quickStop.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "user stop interrupts a running quick download without continuing alias verification");
            }
            var calibrated = Calibration.Combine(example, [example, example with { SpeedMBps = 70 }, example with { SpeedMBps = 65 }]);
            Check(calibrated.Success && calibrated.SpeedMBps == 65 && calibrated.CalibrationRounds == 3 && calibrated.QualityScore < 65,
                "calibration uses median and penalizes unstable low round instead of largest burst");
            Check(!Calibration.Combine(example, [example, example with { Success = false }, example]).Success,
                "incomplete calibration cannot claim three successful rounds");
            var dnsPacket = DnsWire.Query(NetworkScanner.Domains[0], 1234).ToList();
            dnsPacket[2] = 129; dnsPacket[3] = 128; dnsPacket[7] = 1;
            dnsPacket.AddRange([192, 12, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 18, 65, 14, 45]);
            Check(DnsWire.Parse(dnsPacket.ToArray(), 1234).Single() == example.IP, "compressed DNS A answer parses and filters to public IPv4");
            rejected = false;
            try { DnsWire.Parse(dnsPacket.ToArray(), 999); } catch (IOException) { rejected = true; }
            Check(rejected, "unmatched DNS transaction is rejected");
            rejected = false;
            try { DnsWire.Parse(dnsPacket.Take(dnsPacket.Count - 1).ToArray(), 1234); } catch (IOException) { rejected = true; }
            Check(rejected, "truncated DNS answer is rejected");
            using var compressedFixture = new MemoryStream();
            using (var encoder = new System.IO.Compression.GZipStream(compressedFixture, System.IO.Compression.CompressionMode.Compress, true))
                encoder.Write(new byte[1024 * 1024]);
            compressedFixture.Position = 0;
            long wireBytes = 0, decodedBytes = 0;
            using (var counter = new CountingReadStream(compressedFixture, n => wireBytes += n))
            using (var decoder = new System.IO.Compression.GZipStream(counter, System.IO.Compression.CompressionMode.Decompress, true))
            {
                var buffer = new byte[64 * 1024]; int gzipRead;
                while ((gzipRead = decoder.Read(buffer)) > 0) decodedBytes += gzipRead;
            }
            Check(decodedBytes == 1024 * 1024 && wireBytes == compressedFixture.Length && wireBytes < decodedBytes,
                "gzip accounting keeps compressed wire bytes separate from decompressed output");
            double parallelClock = 0;
            var parallelClockGate = new object();
            var parallelSample = ParallelDownload.Measure(example, 3, CancellationToken.None,
                () => new HttpClient(new FakeDownloadHandler(() => { lock (parallelClockGate) parallelClock += .005; })),
                () => { lock (parallelClockGate) return parallelClock; }).GetAwaiter().GetResult();
            Check(parallelSample.Success && Math.Abs(parallelSample.SpeedMBps - 12.5) < .1 && parallelSample.MeasuredSeconds >= 3,
                "three-stream speed uses aggregate bytes over one shared window rather than summing independent windows: " + parallelSample);
            var badParallel = ParallelDownload.Measure(example, 3, CancellationToken.None,
                () => new HttpClient(new FakeDownloadHandler(() => { }, html: true))).GetAwaiter().GetResult();
            Check(!badParallel.Success && badParallel.Bytes == 0, "parallel setup failure cancels the header barrier without hanging");
            using (var stopSource = new CancellationTokenSource())
            using (var unlimited = new ScanBudget(0, stopSource.Token))
            {
                Check(!unlimited.Token.IsCancellationRequested, "precise budget has no automatic timeout");
                stopSource.Cancel();
                Check(unlimited.Token.IsCancellationRequested, "unlimited precise budget still obeys user stop");
            }
            var surveyed = FastScreening.DownloadAsync(example with { Success = false }, CancellationToken.None,
                () => new HttpClient(new FakeDownloadHandler(() => { }))).GetAwaiter().GetResult();
            Check(surveyed.Usable && surveyed.Node.InitialSpeedMBps > 0 && !surveyed.Node.Success && surveyed.Node.RequiredSeconds == 0 && NetworkScanner.RankScore(surveyed.Node) == 0,
                "successful short download only ranks candidates and never becomes an applicable sustained result");
            var badSurvey = FastScreening.DownloadAsync(example, CancellationToken.None,
                () => new HttpClient(new FakeDownloadHandler(() => { }, html: true))).GetAwaiter().GetResult();
            Check(!badSurvey.Usable && badSurvey.Node.Bytes == 0, "short screening rejects HTTP 200 HTML before ranking");
            var fastest = surveyed.Node with { IP = "18.65.14.46", InitialSpeedMBps = 10, TcpLatencyMs = 300 };
            var slower = surveyed.Node with { InitialSpeedMBps = 1, TcpLatencyMs = 10 };
            var ranked = FastScreening.Rank([new(slower, true), new(fastest, true), new(example with { InitialSpeedMBps = 100 }, false)]);
            Check(ranked.Length == 2 && ranked[0].IP == fastest.IP,
                "real download survey outranks low TCP latency and excludes failed downloads");
            var found = LauncherLogInspector.Parse("download url=\"https://resources.eveonline.com/ab/abcdef_01234?token=PRIVATE\"\n" +
                "download url=\"https://newcdn.eveonline.com/patch.bin?token=SECRET\"\n" +
                "login url=\"https://login.eveonline.com/v2/oauth/token\"");
            Check(found.Files[NetworkScanner.Domains[0]].Single() == "https://resources.eveonline.com/ab/abcdef_01234",
                "actual launcher file URLs are detected without exporting query tokens");
            Check(found.Hosts.Contains("newcdn.eveonline.com") && !found.Hosts.Contains("login.eveonline.com"),
                "additional download domains detected without treating authentication as a download");
            var manifest = "app:/bin/test.dll,ab/abc_def,hash,12000000,5000000,33188\napp:/evil,../evil,hash,100,200,1\n";
            var files = NetworkScanner.ParseManifest(manifest);
            Check(files.Count == 1 && files[0].Size == 5000000, "manifest uses compressed size and validates relative path");
            bool loserCancelled = false;
            var read = NetworkScanner.FirstSuccessfulRead(["slow", "fast"], async (choice, token) =>
            {
                if (choice == "fast") { await Task.Delay(15, token); return "valid manifest"; }
                try { await Task.Delay(5000, token); return "slow manifest"; }
                catch (OperationCanceledException) { loserCancelled = true; throw; }
            }, CancellationToken.None).GetAwaiter().GetResult();
            Check(read == "valid manifest" && loserCancelled, "fast successful metadata node wins and slower request is cancelled");
            read = NetworkScanner.FirstSuccessfulRead(["bad", "good"], (choice, _) =>
                choice == "bad" ? Task.FromException<string>(new IOException("unreachable")) : Task.FromResult("good"),
                CancellationToken.None).GetAwaiter().GetResult();
            Check(read == "good", "one failed metadata node does not prevent successful alternative");
            rejected = false;
            try
            {
                NetworkScanner.FirstSuccessfulRead(["bad1", "bad2"], (_, _) => Task.FromException<string>(new IOException("unreachable")),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (IOException) { rejected = true; }
            Check(rejected, "all failed metadata requests terminate instead of retrying indefinitely");
            using (var stopRead = new CancellationTokenSource(25))
            {
                rejected = false;
                try
                {
                    NetworkScanner.FirstSuccessfulRead(["slow"], async (_, token) => { await Task.Delay(5000, token); return "late"; },
                        stopRead.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "user cancellation interrupts metadata reads");
            }
            var backups = NetworkScanner.BackupUrls();
            Check(NetworkScanner.Domains.All(d => new Uri(backups[d]).Scheme == "https" && new Uri(backups[d]).Host == d),
                "fallback files belong to their individual EVE domains");
            Check(backups[EveDomains.BinaryFallback] == EveDomains.Rehost(backups[EveDomains.Binaries], EveDomains.BinaryFallback) &&
                backups[EveDomains.ResourceFallback] == EveDomains.Rehost(backups[EveDomains.Resources], EveDomains.ResourceFallback),
                "new launcher priority CDN aliases get the same real file path under their own HTTPS host");
            var oldPlan = new Dictionary<string, string>(addresses);
            oldPlan.Remove(EveDomains.BinaryFallback);
            rejected = false;
            try { HostsManager.Rewrite(bytes, oldPlan); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "four-host legacy plans cannot silently omit the active fallback download hostname");
            var missingAlias = new Dictionary<string, NodeResult>(calibrationPipeline.Best);
            missingAlias.Remove(EveDomains.BinaryFallback);
            Check(!NetworkScanner.CanApply(calibrationPipeline with { Best = missingAlias }), "missing fallback sustained measurement blocks application");
            missingAlias[EveDomains.BinaryFallback] = calibrationPipeline.Best[EveDomains.Binaries];
            Check(!NetworkScanner.CanApply(calibrationPipeline with { Best = missingAlias }), "sibling hostname success cannot be copied into a fallback result");
            missingAlias[EveDomains.BinaryFallback] = calibrationPipeline.Best[EveDomains.BinaryFallback] with { Success = false };
            Check(!NetworkScanner.CanApply(calibrationPipeline with { Best = missingAlias }), "failed fallback result blocks application even when all keys exist");
            var aliasFiles = new NetworkScanner(_ => { }, _ => { }, (url, _, _) => Task.FromResult(
                url.AbsolutePath.EndsWith(".json") ? "{\"build\":3646012}" :
                url.AbsolutePath.EndsWith(".txt") ? "app:/bin/test.dll,ab/abc_def,hash,12000000,5000000,33188\napp:/resfileindex.txt,cd/cde_fab,hash,100000,50000,33188\n" :
                "res:/texture.dds,ef/ef0_123,hash,20000000,9000000,33188\n"))
                .DiscoverUrls(CancellationToken.None).GetAwaiter().GetResult();
            Check(aliasFiles[EveDomains.BinaryFallback] == "https://binaries-fallback.eveonline.com/ab/abc_def" &&
                aliasFiles[EveDomains.ResourceFallback] == "https://resources-fallback.eveonline.com/ef/ef0_123",
                "current binary and resource manifests refresh both aliases instead of leaving stale backup paths");
            var aliasLog = LauncherLogInspector.Parse("download https://binaries-fallback.eveonline.com/ab/abc_def?token=SECRET\n" +
                "[server-manager] Starting download of build 3646012 for tranquility.\n" +
                "[server-manager] Downloaded 1.53 MiB/17.52 MiB at 53.51 kiB/s\nAuthorization: PRIVATE\n");
            Check(aliasLog.Files[EveDomains.BinaryFallback].Single().EndsWith("/ab/abc_def") && aliasLog.Build == "3646012" &&
                aliasLog.Progress.Single() == "Downloaded 1.53 MiB/17.52 MiB at 53.51 kiB/s",
                "diagnostics extract fallback requests, build and speed without retaining authentication lines or query strings");
            Check(CandidatePlanner.Quick([baselineNode, example with { IP = "3.170.229.35", Source = "关联域名 hosts", LatencyMs = 500 }])
                .Any(n => n.IP == "3.170.229.35"), "existing sibling hosts node reaches fallback download verification despite higher latency");
            var familyCache = NetworkScanner.Domains.ToDictionary(d => d, _ => new Dictionary<string, string>());
            familyCache[EveDomains.Binaries]["3.170.229.35"] = "系统 DNS / 当前 hosts";
            familyCache[EveDomains.BinaryFallback]["3.170.229.35"] = "腾讯 DNS";
            EveDomains.MergeFamilyCandidates(familyCache);
            Check(familyCache[EveDomains.BinaryFallback]["3.170.229.35"].Contains("关联域名 hosts") &&
                familyCache[EveDomains.BinaryFallback]["3.170.229.35"].Contains("腾讯 DNS"),
                "sibling hosts provenance survives even when that IP already appeared in fallback public DNS");
            var owner = new Dictionary<int, string> { [1234] = "eve-online" };
            byte[] TcpFixture(bool ipv6)
            {
                var table = new byte[4 + (ipv6 ? 56 : 24)];
                BitConverter.GetBytes(1).CopyTo(table, 0);
                BitConverter.GetBytes(5).CopyTo(table, 4 + (ipv6 ? 48 : 0));
                BitConverter.GetBytes(1234).CopyTo(table, 4 + (ipv6 ? 52 : 20));
                System.Net.IPAddress.Parse(ipv6 ? "2001:4860:4860::8888" : "3.170.229.35").GetAddressBytes().CopyTo(table, 4 + (ipv6 ? 24 : 12));
                table[4 + (ipv6 ? 44 : 16)] = 1; table[4 + (ipv6 ? 45 : 17)] = 187;
                return table;
            }
            Check(LauncherConnections.ParseTable(TcpFixture(false), false, owner).Single() is { RemoteIP: "3.170.229.35", RemotePort: 443, PID: 1234 },
                "launcher process IPv4 table reads PID, network byte order and remote address correctly");
            Check(LauncherConnections.ParseTable(TcpFixture(true), true, owner).Single() is { RemoteIP: "2001:4860:4860::8888", RemotePort: 443 },
                "launcher process IPv6 connections are visible instead of silently attributing IPv4 helper tests to the launcher");
            Check(LauncherConnections.ParseTable(TcpFixture(false), false, new Dictionary<int, string>()).Count == 0,
                "connection inspection excludes unrelated processes");
            rejected = false;
            try { LauncherConnections.ParseTable(TcpFixture(false)[..10], false, owner); } catch (IOException) { rejected = true; }
            Check(rejected, "truncated native connection tables are rejected");
            var localListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            try
            {
                localListener.Start();
                using var localClient = new System.Net.Sockets.TcpClient();
                localClient.Connect((System.Net.IPEndPoint)localListener.LocalEndpoint);
                using var acceptedClient = localListener.AcceptTcpClient();
                var nativeRows = LauncherConnections.Snapshot(new Dictionary<int, string> { [Environment.ProcessId] = "fixture" }, false);
                Check(nativeRows.Any(c => c.PID == Environment.ProcessId && c.RemoteIP == "127.0.0.1" &&
                    c.RemotePort == ((System.Net.IPEndPoint)localListener.LocalEndpoint).Port),
                    "real Windows TCP API identifies an established loopback connection owned by the calling process");
            }
            finally { localListener.Stop(); }
            bool slowAliasCancelled = false;
            var racedManifests = new NetworkScanner(_ => { }, _ => { }, async (url, _, token) =>
            {
                if (url.Host == EveDomains.BinaryFallback)
                {
                    try { await Task.Delay(5000, token); }
                    catch (OperationCanceledException) { slowAliasCancelled = true; throw; }
                }
                return url.AbsolutePath.EndsWith(".json") ? "{\"build\":3646012}" : "app:/bin/test.dll,ab/abc_def,hash,12000000,5000000,33188\n";
            }).DiscoverUrls(CancellationToken.None, 2).GetAwaiter().GetResult();
            Check(slowAliasCancelled && racedManifests[EveDomains.BinaryFallback].EndsWith("/ab/abc_def"),
                "slow priority CDN metadata does not exhaust the deadline before a fast sibling manifest can be used");
            Check(EveDomains.All.All(d => EveDomains.ProxyRules.Contains("DOMAIN," + d + ",")), "VPN export covers every supported download and launcher hostname");
            var fallback = new NetworkScanner(_ => { }, _ => { }, (_, _, _) =>
                Task.FromException<string>(new IOException("all metadata endpoints unreachable")))
                .DiscoverUrls(CancellationToken.None).GetAwaiter().GetResult();
            Check(NetworkScanner.Domains.All(d => fallback[d] == backups[d]),
                "unreachable version metadata falls back for both domains instead of aborting scan");
            using (var stopDiscovery = new CancellationTokenSource())
            {
                stopDiscovery.Cancel();
                rejected = false;
                try
                {
                    new NetworkScanner(_ => { }, _ => { }, (_, _, token) => Task.FromCanceled<string>(token))
                        .DiscoverUrls(stopDiscovery.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "cancelled discovery does not start fallback downloads");
            }

            var hosts = Path.Combine(directory, "hosts");
            File.WriteAllBytes(hosts, bytes);
            var mappingExample = ConnectionDiagnostics.ReadMappings(hosts);
            Check(mappingExample[NetworkScanner.Domains[0]].Single() == "1.2.3.4" && mappingExample[NetworkScanner.Domains[1]].Single() == "5.6.7.8",
                "diagnostic hosts parsing handles comments and uppercase aliases");
            HostsManager.Apply(hosts, addresses);
            var backup = Directory.GetFiles(directory, "hosts.evehelper-*.bak").Single();
            Check(File.ReadAllBytes(backup).SequenceEqual(bytes), "backup matches original bytes");
            HostsManager.Apply(hosts, addresses);
            Check(Directory.GetFiles(directory, "hosts.evehelper-*.bak").Length == 1, "no-op does not create new backup");
            File.AppendAllText(hosts, "8.8.8.8 new.example\r\n");
            var concurrent = File.ReadAllBytes(hosts);
            rejected = false;
            try { HostsManager.Restore(hosts); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(hosts).SequenceEqual(concurrent), "restore refuses to overwrite later unrelated edits");
            File.WriteAllBytes(hosts, output);
            HostsManager.Restore(hosts);
            Check(File.ReadAllBytes(hosts).SequenceEqual(bytes), "restore recovers original bytes");
            Check(!File.Exists(hosts + ".evehelper.json") && File.Exists(backup), "restore clears state and keeps backup");
            rejected = false;
            try { HostsManager.Restore(hosts); } catch (IOException) { rejected = true; }
            Check(rejected, "restore without a saved operation is rejected");
            var invalidAddresses = new Dictionary<string, string>(addresses) { [NetworkScanner.Domains[0]] = "1.2.3.999" };
            rejected = false;
            try { HostsManager.Apply(hosts, invalidAddresses); } catch (ArgumentException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(hosts).SequenceEqual(bytes), "invalid plan never changes hosts");
            var readOnlyPath = Path.Combine(directory, "readonly-hosts");
            File.WriteAllBytes(readOnlyPath, bytes);
            File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
            rejected = false;
            try { HostsManager.Apply(readOnlyPath, addresses); } catch (UnauthorizedAccessException) { rejected = true; }
            finally { File.SetAttributes(readOnlyPath, FileAttributes.Normal); }
            Check(rejected && File.ReadAllBytes(readOnlyPath).SequenceEqual(bytes), "write failure preserves original hosts");
            return string.Join(Environment.NewLine, report) + $"\r\nTOTAL: {report.Count} checks passed\r\n";
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
