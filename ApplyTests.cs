using System.Diagnostics;
using System.Text;

namespace EveUpdateHelper;

internal static class ApplyTests
{
    internal static string Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "EveApplyTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTime.Now;
            var urls = NetworkScanner.BackupUrls();
            var nodes = NetworkScanner.DownloadDomains.Select((domain, index) => new NodeResult(domain,
                index % 2 == 0 ? "3.170.229.33" : "18.155.64.30", "offline fixture", true, 50, 15,
                10 * 1024 * 1024, "", "full sustained fixture", urls[domain], 10, .2, RequiredSeconds: 10)).ToList();
            var plan = new ApplyPlan(nodes, now);
            bool Reject(Action action)
            {
                try { action(); return false; }
                catch (IOException) { return true; }
            }
            const string original = "# unrelated settings\r\n127.0.0.1 localhost\r\n" +
                "65.8.76.115 resources-fallback.eveonline.com\r\n65.8.76.115 binaries-fallback.eveonline.com\r\n" +
                "18.155.64.30 resources-fallback.eveonline.com\r\n18.155.64.30 BINARIES-FALLBACK.EVEONLINE.COM other.example # keep comment\r\n" +
                "2001:4860::8888 binaries-fallback.eveonline.com\r\n" +
                "# BEGIN EVE UPDATE HELPER\r\n18.155.64.30 resources.eveonline.com\r\n3.170.229.33 binaries.eveonline.com\r\n" +
                "13.35.238.8 launcher.eveonline.com\r\n13.35.202.129 launcher.ccpgames.com\r\n# END EVE UPDATE HELPER\r\n";
            var originalBytes = Encoding.UTF8.GetBytes(original);
            var path = Path.Combine(directory, "hosts");
            File.WriteAllBytes(path, originalBytes);
            var stages = new List<string>();
            var clock = Stopwatch.StartNew();
            var message = ApplyWorkflow.Apply(path, plan, now, () => false, (_, stage, _) => stages.Add(stage));
            clock.Stop();
            var actual = HostsManager.ReadMappings(File.ReadAllBytes(path));
            check(nodes.All(n => actual[n.Domain].SequenceEqual(new[] { n.IP })),
                "real local apply cleans duplicate fallback lines outside the old four-domain block, including mixed case and IPv6");
            var applied = File.ReadAllText(path);
            check(applied.Contains("18.155.64.30 other.example # keep comment") && applied.Contains("127.0.0.1 localhost") &&
                actual[NetworkScanner.Domains[2]].Single() == "13.35.238.8",
                "apply preserves unrelated aliases, comments, localhost and an untested optional launcher mapping");
            check(stages.SequenceEqual(new[] { "备份并整理 hosts", "核对已写入的节点" }),
                "apply workflow completes using only local write and verification stages");
            var backup = Directory.GetFiles(directory, "hosts.evehelper-*.bak").Single();
            check(File.ReadAllBytes(backup).SequenceEqual(originalBytes), "duplicate cleanup backs up exact original bytes");
            check(Reject(() => HostsManager.VerifyMappings(originalBytes, nodes.ToDictionary(n => n.Domain, n => n.IP))),
                "local postcondition rejects duplicate or wrong mappings instead of reporting success");
            var duplicateSameIP = File.ReadAllBytes(path).Concat(Encoding.ASCII.GetBytes(nodes[0].IP + " " + nodes[0].Domain + "\r\n")).ToArray();
            check(Reject(() => HostsManager.VerifyMappings(duplicateSameIP, nodes.ToDictionary(n => n.Domain, n => n.IP))),
                "duplicate identical IP entries also fail single-mapping verification");
            ApplyWorkflow.Apply(path, plan, now, () => false);
            check(Directory.GetFiles(directory, "hosts.evehelper-*.bak").Length == 1, "applying the same verified plan is idempotent without extra backup");
            HostsManager.Restore(path);
            check(File.ReadAllBytes(path).SequenceEqual(originalBytes), "restoring the cleanup returns all original bytes and duplicate lines");

            void RejectedPlan(ApplyPlan invalid, string reason, bool running = false)
            {
                check(Reject(() => ApplyWorkflow.Apply(path, invalid, now, () => running)) &&
                    File.ReadAllBytes(path).SequenceEqual(originalBytes) && !File.Exists(path + ".evehelper.json"), reason);
            }
            RejectedPlan(plan with { TestedAt = now.AddMinutes(-31) }, "expired results cannot write hosts even without network retesting");
            RejectedPlan(plan with { TestedAt = now.AddMinutes(2) }, "future-dated result cannot bypass the validity window");
            RejectedPlan(plan with { Nodes = nodes.Where(n => n.Domain != EveDomains.BinaryFallback).ToList() }, "missing fallback result prevents all writes");
            RejectedPlan(plan with { Nodes = nodes.Append(nodes[0]).ToList() }, "duplicate result records cannot overwrite independent domain validation");
            RejectedPlan(plan with { Nodes = nodes.Select((n, i) => i == 0 ? n with { SmallFileMBps = 0 } : n).ToList() }, "failed small request still prevents apply");
            RejectedPlan(plan with { Nodes = nodes.Select((n, i) => i == 0 ? n with { MeasuredSeconds = 1 } : n).ToList() }, "incomplete sustained download cannot be applied");
            RejectedPlan(plan with { Nodes = nodes.Select((n, i) => i == 0 ? n with { TestUrl = urls[EveDomains.Resources] } : n).ToList() }, "cross-host copied test URL cannot be applied");
            RejectedPlan(plan, "running launcher blocks local application before changes", running: true);
            foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
            {
                var input = encoding.GetPreamble().Concat(encoding.GetBytes(original)).ToArray();
                var cleaned = HostsManager.Rewrite(input, nodes.ToDictionary(n => n.Domain, n => n.IP));
                HostsManager.VerifyMappings(cleaned, nodes.ToDictionary(n => n.Domain, n => n.IP));
                check(HostsManager.ReadMappings(cleaned)[EveDomains.BinaryFallback].Count == 1,
                    "duplicate verification supports BOM encoding: " + encoding.WebName);
            }
            return $"INFO: Offline apply on temporary hosts: {clock.Elapsed.TotalMilliseconds:F2} ms; backup, deduplication and local verification completed without a network request.";
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
