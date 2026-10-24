using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace EveUpdateHelper;

internal static class ConnectionDiagnostics
{
    internal static List<string> LauncherProcesses()
    {
        return LauncherConnections.Processes().Select(p => p.Value + " (PID " + p.Key + ")").ToList();
    }

    internal static Dictionary<string, List<string>> ReadMappings(string hostsPath)
    {
        var mappings = NetworkScanner.Domains.ToDictionary(d => d, _ => new List<string>());
        foreach (var line in File.ReadAllLines(hostsPath))
        {
            var body = line.Split('#')[0].Trim();
            var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _)) continue;
            foreach (var name in parts.Skip(1))
                if (mappings.TryGetValue(name.ToLowerInvariant(), out var addresses)) addresses.Add(parts[0]);
        }
        return mappings;
    }

    internal static async Task<string> InspectAsync(IReadOnlyDictionary<string, NodeResult>? chosen,
        Action<string>? progress, CancellationToken ct)
    {
        var report = new StringBuilder();
        void Log(string message) { report.AppendLine(message); progress?.Invoke(message); }
        Log("生效诊断：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var recent = LauncherLogInspector.ReadRecent();
        Log("新版默认优先 CDN：binaries-fallback.eveonline.com、resources-fallback.eveonline.com。本版分别测速并覆盖。");
        Log(recent.Hosts.Count == 0 ? "未找到本机启动器近期下载域名记录。" : "启动器日志近期请求域名：" + string.Join(", ", recent.Hosts));
        foreach (var host in recent.Hosts.Where(h => !NetworkScanner.Domains.Contains(h)))
            Log($"日志包含当前未优选的下载域名 {host}，需要进一步核对该请求。");
        if (recent.Build != null) Log("近期日志中的游戏补丁版本：" + recent.Build);
        foreach (var sample in recent.Progress) Log("近期日志下载记录（可能已结束）：" + sample);
        var processes = LauncherConnections.Processes();
        var running = processes.Select(p => p.Value + " (PID " + p.Key + ")").ToList();
        Log(running.Count == 0 ? "未发现运行中的 EVE 启动器进程。" :
            "EVE 启动器仍在运行，可能保留旧连接 / DNS 缓存：" + string.Join(", ", running));
        var mappings = ReadMappings(HostsManager.SystemHosts);
        if (mappings[EveDomains.Resources].Count > 0 && mappings[EveDomains.ResourceFallback].Count == 0 ||
            mappings[EveDomains.Binaries].Count > 0 && mappings[EveDomains.BinaryFallback].Count == 0)
            Log("发现旧版规则遗漏：普通下载域名已固定，但对应 -fallback 域名未设置；新版可能继续走默认慢节点。");
        foreach (bool ipv6 in new[] { false, true })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var connections = LauncherConnections.Snapshot(processes, ipv6);
                if (connections.Count == 0) Log($"启动器当前没有已建立的 IPv{(ipv6 ? 6 : 4)} TCP 连接。");
                foreach (var connection in connections)
                {
                    var matches = mappings.Where(p => p.Value.Contains(connection.RemoteIP)).Select(p => p.Key).ToArray();
                    Log($"启动器自身连接：{connection.Process} PID {connection.PID} → {connection.RemoteIP}:{connection.RemotePort}" +
                        (matches.Length > 0 ? "；IP 与 hosts 节点一致：" + string.Join(", ", matches) : "；IP 未匹配当前 EVE hosts 节点"));
                }
            }
            catch (Exception ex) { Log("读取启动器连接失败：" + NetworkScanner.ErrorText(ex)); }
        }
        Log("连接表只显示进程和远端 IP，不能单独确定 HTTPS 域名或 URL；登录等连接也可能出现在其中。");
        var fallback = NetworkScanner.BackupUrls();
        foreach (var domain in NetworkScanner.Domains)
        {
            ct.ThrowIfCancellationRequested();
            var node = chosen != null && chosen.TryGetValue(domain, out var selected) ? selected : null;
            string? expected = node?.IP ?? (mappings[domain].Count == 1 ? mappings[domain][0] : null);
            Log($"{domain}：hosts = {(mappings[domain].Count == 0 ? "未设置" : string.Join(", ", mappings[domain]))}");
            if (node != null && !mappings[domain].Contains(node.IP)) Log("本轮推荐节点尚未写入 hosts；请先应用节点。");
            if (mappings[domain].Distinct().Count() > 1) Log("发现重复或 IPv6 映射，需要核对实际连接地址。");
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain, ct).WaitAsync(TimeSpan.FromSeconds(8), ct);
                Log("系统实际解析：" + string.Join(", ", addresses.Select(a => a.ToString())));
                string remote = "未建立连接";
                using var client = NetworkScanner.Client(connected: ip => remote = ip);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                if (domain == NetworkScanner.Domains[2])
                    await NetworkScanner.CheckLauncher(client, "系统默认", "实际连接诊断", timeout.Token);
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, node?.TestUrl ?? fallback[domain]);
                    request.Headers.Range = new RangeHeaderValue(0, 128 * 1024 - 1);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    NetworkScanner.ValidateFileResponse(response);
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    var buffer = new byte[8192];
                    long bytes = 0;
                    while (bytes < 128 * 1024)
                    {
                        int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 128 * 1024 - bytes)), timeout.Token);
                        if (n == 0) break;
                        bytes += n;
                    }
                    if (bytes < 128 * 1024) throw new IOException("实际连接下载不足 128KB。");
                }
                Log("助手自身测试连接（不强制 IP、直连）：" + remote);
                if (expected != null) Log(remote == expected ? "助手连接已命中 hosts / 推荐 IP；启动器连接请对照上方进程记录。" :
                    "助手连接未命中推荐 IP，请检查 DNS 缓存、IPv6 或代理分流。");
                else Log("目前使用正常 DNS；没有应用固定节点。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log("实际连接检查失败：" + NetworkScanner.ErrorText(ex)); }
        }
        Log("DNS 刷新不会关闭旧连接。应用后应从托盘完全退出启动器并重新打开；本工具不删除 SharedCache 或账号缓存。");
        Log("如使用 VPN：两个 -fallback 域名也须加入规则；只设置系统 HTTP 代理未必接管启动器下载，需检查 TUN/进程分流。助手测速为直连。");
        Log("若仍慢：启动器设置 → Tools → Open log directory，日志可确认真实请求域名；EVE Online → 检查更新 / 验证游戏文件。");
        return report.ToString();
    }
}
