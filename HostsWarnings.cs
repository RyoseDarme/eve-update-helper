namespace EveUpdateHelper;

internal sealed partial class MainForm
{
    void WarnDuplicateMappings()
    {
        try
        {
            var duplicates = ConnectionDiagnostics.ReadMappings(HostsManager.SystemHosts).Where(p => p.Value.Count > 1).ToArray();
            foreach (var pair in duplicates)
                Log($"发现重复 hosts：{pair.Key} → {string.Join(", ", pair.Value)}；应用本轮节点时会统一整理为一条。");
        }
        catch (IOException) { Log("暂时无法读取 hosts；应用时将再次检查文件。"); }
        catch (UnauthorizedAccessException) { Log("暂时无权读取 hosts；应用时将请求管理员权限。"); }
    }
}
