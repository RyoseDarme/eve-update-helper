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

    internal static Dictionary<string, List<string>> ReadMappings(byte[] bytes)
    {
        var mappings = NetworkScanner.Domains.ToDictionary(d => d, _ => new List<string>());
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !System.Net.IPAddress.TryParse(parts[0], out var address)) continue;
            foreach (var name in parts.Skip(1))
                if (mappings.TryGetValue(name.ToLowerInvariant(), out var ips)) ips.Add(address.ToString());
        }
        return mappings;
    }

    internal static void VerifyMappings(byte[] bytes, IReadOnlyDictionary<string, string> expected)
    {
        var actual = ReadMappings(bytes);
        foreach (var (domain, ip) in expected)
            if (!actual.TryGetValue(domain, out var found) || found.Count != 1 || found[0] != ip)
                throw new IOException($"{domain} 写入核对失败：必须仅保留一条本轮已验证的 IP。");
    }

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
        VerifyMappings(after, addresses);
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
            var stored = File.ReadAllBytes(hostsPath);
            if (!stored.SequenceEqual(after)) throw new IOException("hosts 写入后被其他程序修改，请检查文件。");
            VerifyMappings(stored, addresses);
            File.Move(stateTemp, statePath, overwrite: true);
            return $"{addresses.Count} 个 EVE 更新域名已写入，旧记录已整理，每域仅保留一条已验证 IP。\r\n备份：" + backup;
        }
        catch
        {
            if (replaced && File.ReadAllBytes(hostsPath).SequenceEqual(after))
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
            if (operation == "--apply")
            {
                if (new FileInfo(planPath).Length > 64 * 1024) throw new IOException("应用记录大小异常。");
                var plan = JsonSerializer.Deserialize<ApplyPlan>(File.ReadAllText(planPath)) ?? throw new IOException("应用记录损坏。");
                message = ApplyWorkflow.Apply(HostsManager.SystemHosts, plan, DateTime.Now,
                    () => ConnectionDiagnostics.LauncherProcesses().Count > 0, Progress);
            }
            else { Progress(55, "恢复原设置", "正在校验备份并恢复 hosts…"); message = HostsManager.Restore(HostsManager.SystemHosts); }
            Progress(70, "刷新 DNS 缓存", "本地写入核对完成，正在刷新系统 DNS；无需再次下载测速。" );
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
            message += "\r\n应用未进行联网复测。请重新打开启动器；需要排查下载时再点击「下载诊断」。";
            File.WriteAllText(output, JsonSerializer.Serialize(new OperationResult(true, message), Program.JsonOptions));
            Progress(100, "操作完成", "请重新打开 EVE 启动器。已核对 hosts，无需重复测速。" );
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new OperationResult(false, ex.Message), Program.JsonOptions));
            Environment.ExitCode = 1;
        }
    }
}
