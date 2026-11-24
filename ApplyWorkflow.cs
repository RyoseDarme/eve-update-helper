using System.Diagnostics;

namespace EveUpdateHelper;

// Applying a verified plan is a local file operation. Network diagnostics are an
// explicit action in the main window, never a prerequisite for applying.
internal static class ApplyWorkflow
{
    internal static Dictionary<string, string> Validate(ApplyPlan plan, DateTime now)
    {
        if (plan.Nodes == null || plan.Nodes.Count > NetworkScanner.Domains.Length || plan.Nodes.Any(n => n == null))
            throw new IOException("应用记录损坏。");
        var age = now - plan.TestedAt;
        if (age.TotalMinutes > 30 || age.TotalSeconds < -60)
            throw new IOException("测速结果已过期，请重新测速；有效期为30分钟。");
        if (!NetworkScanner.DownloadDomains.All(d => plan.Nodes.Count(n => n.Domain == d) == 1) ||
            plan.Nodes.Any(n => !NetworkScanner.Domains.Contains(n.Domain)) || plan.Nodes.GroupBy(n => n.Domain).Any(g => g.Count() != 1))
            throw new IOException("必须有五个下载域名（含两个 -fallback）的独立持续测速结果。");
        if (!NetworkScanner.CanApply(new ScanResult(plan.Nodes, plan.Nodes.ToDictionary(n => n.Domain), plan.TestedAt)))
            throw new IOException("应用记录包含未通过持续下载、小请求或域名验证的节点，请重新测速。");
        if (plan.Nodes.Any(n => !n.Success || !NetworkScanner.PublicIPv4(n.IP)))
            throw new IOException("应用记录包含无效节点。");
        return plan.Nodes.ToDictionary(n => n.Domain, n => n.IP);
    }

    internal static string Apply(string hostsPath, ApplyPlan plan, DateTime now, Func<bool> launcherRunning,
        Action<int, string, string>? progress = null)
    {
        var watch = Stopwatch.StartNew();
        var addresses = Validate(plan, now);
        if (launcherRunning()) throw new IOException("EVE 启动器仍在运行（可能缩到托盘）。请从托盘完全退出，再应用节点。");
        var before = HostsManager.ReadMappings(File.ReadAllBytes(hostsPath));
        var duplicateDomains = before.Where(p => addresses.ContainsKey(p.Key) && p.Value.Count > 1).Select(p => p.Key).ToArray();
        progress?.Invoke(35, "备份并整理 hosts", duplicateDomains.Length == 0 ?
            "直接使用本轮已通过的测速结果，替换旧节点。" : "正在清理重复记录：" + string.Join("、", duplicateDomains));
        string message = HostsManager.Apply(hostsPath, addresses);
        progress?.Invoke(65, "核对已写入的节点", "仅核对本地 hosts；每个域名须仅有一条推荐 IP。");
        HostsManager.VerifyMappings(File.ReadAllBytes(hostsPath), addresses);
        return message + $"\r\n本地写入核对通过，用时 {watch.Elapsed.TotalSeconds:F2} 秒；未重复下载测速。";
    }
}
