using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EveUpdateHelper;

internal sealed record ApplyPlan(List<NodeResult> Nodes, DateTime TestedAt);
internal sealed record OperationResult(bool Success, string Message);
internal sealed record BackupState(string BackupPath, string AppliedHash);

internal static class HostsManager
{
    const string Start = "# BEGIN EVE UPDATE HELPER";
    const string End = "# END EVE UPDATE HELPER";
    internal static string SystemHosts => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static byte[] Rewrite(byte[] bytes, IReadOnlyDictionary<string, string> addresses)
    {
        if (!NetworkScanner.DownloadDomains.All(addresses.ContainsKey) ||
            addresses.Any(p => !NetworkScanner.Domains.Contains(p.Key) || !NetworkScanner.PublicIPv4(p.Value)))
            throw new ArgumentException("必须提供五个下载域名（包括两个 -fallback）的独立有效 IPv4。");
        Encoding encoding = Encoding.Latin1;
        int offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) { encoding = new UTF8Encoding(true); offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 255, 254 })) { encoding = Encoding.Unicode; offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 254, 255 })) { encoding = Encoding.BigEndianUnicode; offset = 2; }
        var original = encoding.GetString(bytes, offset, bytes.Length - offset);
        var newline = original.Contains('\n') && !original.Contains("\r\n") ? "\n" : "\r\n";
        var output = new StringBuilder();
        bool managed = false;
        foreach (Match line in Regex.Matches(original, @"[^\r\n]*(?:\r\n|\n|\r|$)"))
        {
            if (line.Length == 0) continue;
            var ending = Regex.Match(line.Value, @"(\r\n|\n|\r)$").Value;
            var body = line.Value[..(line.Length - ending.Length)];
            if (body.Trim() == Start)
            {
                if (managed) throw new IOException("hosts 管理标记重复，请检查文件。");
                managed = true;
                continue;
            }
            if (body.Trim() == End)
            {
                if (!managed) throw new IOException("hosts 管理标记不完整，请检查文件。");
                managed = false;
                continue;
            }
            // Even in an existing block, preserve unrelated lines if someone edited it.
            var m = Regex.Match(body, @"^(\s*)(\S+)([ \t]+)([^#]*)(#.*)?$");
            if (!m.Success || body.TrimStart().StartsWith('#')) { output.Append(line.Value); continue; }
            var names = Regex.Split(m.Groups[4].Value.Trim(), @"\s+");
            var others = names.Where(n => !addresses.Keys.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (others.Length == names.Length) { output.Append(line.Value); continue; }
            if (others.Length > 0)
                output.Append(m.Groups[1].Value).Append(m.Groups[2].Value).Append(m.Groups[3].Value)
                    .Append(string.Join(' ', others)).Append(' ').Append(m.Groups[5].Value).Append(ending);
            else if (m.Groups[5].Success)
                output.Append(m.Groups[1].Value).Append(m.Groups[5].Value).Append(ending);
        }
        if (managed) throw new IOException("hosts 管理标记不完整，未修改文件。");
        if (output.Length > 0 && output[^1] is not ('\n' or '\r')) output.Append(newline);
        output.Append(Start).Append(newline);
        foreach (var domain in NetworkScanner.Domains.Where(addresses.ContainsKey)) output.Append(addresses[domain]).Append(' ').Append(domain).Append(newline);
        output.Append(End).Append(newline);
        return encoding.GetPreamble().Concat(encoding.GetBytes(output.ToString())).ToArray();
    }

    internal static string Apply(string hostsPath, Dictionary<string, string> addresses)
    {
        var before = File.ReadAllBytes(hostsPath);
        var after = Rewrite(before, addresses);
        if (before.SequenceEqual(after)) return "hosts 已使用本次节点，无需重复写入。";
        var suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        var backup = hostsPath + ".evehelper-" + suffix + ".bak";
        var temp = hostsPath + ".evehelper-" + suffix + ".tmp";
        var statePath = hostsPath + ".evehelper.json";
        var stateTemp = statePath + "." + suffix;
        bool replaced = false;
        try
        {
            File.WriteAllBytes(temp, after);
            File.WriteAllText(stateTemp, JsonSerializer.Serialize(new BackupState(backup, Hash(after)), Program.JsonOptions));
            if (!before.SequenceEqual(File.ReadAllBytes(hostsPath))) throw new IOException("hosts 在操作期间被其他程序修改，请重试。");
            File.Replace(temp, hostsPath, backup);
            replaced = true;
            File.Move(stateTemp, statePath, overwrite: true);
            return $"{addresses.Count} 个 EVE 更新域名已写入。\r\n备份：" + backup;
        }
        catch
        {
            if (replaced)
            {
                File.WriteAllBytes(temp, before);
                File.Replace(temp, hostsPath, null);
            }
            throw;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(stateTemp)) File.Delete(stateTemp);
        }
    }

    internal static string Restore(string hostsPath)
    {
        var statePath = hostsPath + ".evehelper.json";
        if (!File.Exists(statePath)) throw new IOException("没有本工具保存的恢复记录；尚未应用过节点。" );
        var state = JsonSerializer.Deserialize<BackupState>(File.ReadAllText(statePath)) ?? throw new IOException("恢复记录损坏。");
        var backup = Path.GetFullPath(state.BackupPath);
        if (Path.GetDirectoryName(backup) != Path.GetDirectoryName(Path.GetFullPath(hostsPath)) ||
            !Path.GetFileName(backup).StartsWith(Path.GetFileName(hostsPath) + ".evehelper-", StringComparison.Ordinal) ||
            !backup.EndsWith(".bak", StringComparison.Ordinal) || !File.Exists(backup)) throw new IOException("备份不存在或路径异常。");
        if (Hash(File.ReadAllBytes(hostsPath)) != state.AppliedHash)
            throw new IOException("应用节点后，hosts 又被其他程序修改。为保留这些修改，已停止恢复。\r\n原备份：" + backup);
        var temp = hostsPath + ".evehelper-restore-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(backup, temp);
            if (Hash(File.ReadAllBytes(hostsPath)) != state.AppliedHash) throw new IOException("hosts 已发生变化，请重试。");
            File.Replace(temp, hostsPath, null);
            File.Delete(statePath);
            return "已恢复至最近一次应用之前的 hosts。原备份已保留。";
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal static class ElevatedOperation
{
    internal static async Task RunAsync(string operation, string planPath)
    {
        var output = planPath + ".result.json";
        void Progress(int percent, string stage, string detail)
        {
            try
            {
                var path = planPath + ".progress.json";
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new OperationProgress(percent, stage, detail), Program.JsonOptions));
                File.Move(temp, path, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try
        {
            Progress(5, "检查操作条件", "正在检查管理员权限和测速结果…");
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("写入或恢复 hosts 需要管理员权限。");
            string message;
            IReadOnlyDictionary<string, NodeResult>? selectedForDiagnostics = null;
            if (operation == "--apply")
            {
                if (new FileInfo(planPath).Length > 64 * 1024) throw new IOException("应用记录大小异常。");
                var plan = JsonSerializer.Deserialize<ApplyPlan>(File.ReadAllText(planPath)) ?? throw new IOException("应用记录损坏。");
                var age = DateTime.Now - plan.TestedAt;
                if (age.TotalMinutes > 30 || age.TotalSeconds < -60) throw new IOException("测速结果已过期，请重新测速。");
                if (!NetworkScanner.DownloadDomains.All(d => plan.Nodes.Count(n => n.Domain == d) == 1) ||
                    plan.Nodes.Any(n => !NetworkScanner.Domains.Contains(n.Domain)) || plan.Nodes.GroupBy(n => n.Domain).Any(g => g.Count() != 1))
                    throw new IOException("必须有五个文件下载域名（含两个 -fallback）的独立持续测速结果；旧启动器界面域名可选。");
                if (!NetworkScanner.CanApply(new ScanResult(plan.Nodes, plan.Nodes.ToDictionary(n => n.Domain), plan.TestedAt)))
                    throw new IOException("应用记录包含未完整通过持续下载、小请求或域名验证的节点，请重新测速。");
                if (ConnectionDiagnostics.LauncherProcesses().Count > 0)
                    throw new IOException("EVE 启动器仍在运行（可能缩到托盘）。请从托盘完全退出启动器，再应用节点。");
                int checkedNodes = 0;
                int CheckPercent() => 10 + checkedNodes * 40 / plan.Nodes.Count;
                foreach (var node in plan.Nodes)
                {
                    Progress(CheckPercent(), $"复核节点 {checkedNodes + 1}/{plan.Nodes.Count}",
                        $"{node.Domain}\r\n确认该节点仍可下载，尚未修改 hosts。每个节点可能需要数秒。" );
                    if (!node.Success || (node.Domain != NetworkScanner.Domains[2] &&
                        (node.Bytes < 128 * 1024 || !double.IsFinite(node.SpeedMBps) || node.SpeedMBps <= 0 ||
                            node.RequiredSeconds is not (8 or 10 or 12 or 15 or 20 or 30) || node.MeasuredSeconds < node.RequiredSeconds - .5)))
                        throw new IOException("不能应用未成功下载的节点。");
                    var check = await NetworkScanner.TestNode(node.Domain, node.IP, "应用前复核", node.TestUrl, true, CancellationToken.None, 3,
                        progress: s => Progress(CheckPercent() + 3, $"复核节点 {checkedNodes + 1}/{plan.Nodes.Count}", s),
                        additionalUrls: node.AdditionalUrls,
                        sampleProgress: n => Progress(CheckPercent() + Math.Clamp((int)(n.MeasuredSeconds / 3 * 4), 0, 4),
                            $"复核节点 {checkedNodes + 1}/{plan.Nodes.Count}", node.Domain + "\r\n" + n.Detail));
                    if (!check.Success) throw new IOException($"{node.Domain} 应用前复核失败：{check.Detail}。hosts 未修改。");
                    checkedNodes++;
                }
                Progress(55, "备份并写入 hosts", "正在保存原设置，分别写入通过验证的下载节点…");
                message = HostsManager.Apply(HostsManager.SystemHosts, plan.Nodes.ToDictionary(n => n.Domain, n => n.IP));
                selectedForDiagnostics = plan.Nodes.ToDictionary(n => n.Domain, n => n);
            }
            else { Progress(55, "恢复原设置", "正在校验备份并恢复 hosts…"); message = HostsManager.Restore(HostsManager.SystemHosts); }
            Progress(70, "刷新 DNS 缓存", "hosts 操作完成，正在刷新系统 DNS。之后将检查实际连接地址。" );
            try
            {
                using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"), "/flushdns")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                await Task.WhenAll(stdout, stderr);
                message += process.ExitCode == 0 ? "\r\nDNS 缓存已刷新，请完全退出并重新打开 EVE 启动器。" : "\r\nDNS 刷新失败，请手动运行 ipconfig /flushdns。";
            }
            catch { message += "\r\nDNS 刷新失败，请手动运行 ipconfig /flushdns。"; }
            Progress(80, "检查实际连接", "正在核对系统解析和 HTTPS 实际连接地址。\r\n连接较慢时，请等待检查结束。" );
            message += "\r\n" + await ConnectionDiagnostics.InspectAsync(selectedForDiagnostics,
                s => Progress(85, "检查实际连接", s), CancellationToken.None);
            File.WriteAllText(output, JsonSerializer.Serialize(new OperationResult(true, message), Program.JsonOptions));
            Progress(100, "操作完成", "请重新打开 EVE 启动器。详细连接检查结果见主窗口日志。" );
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new OperationResult(false, ex.Message), Program.JsonOptions));
            Environment.ExitCode = 1;
        }
    }
}
