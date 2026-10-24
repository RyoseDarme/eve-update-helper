namespace EveUpdateHelper;

internal static class EveDomains
{
    internal const string Resources = "resources.eveonline.com";
    internal const string Binaries = "binaries.eveonline.com";
    internal const string ResourceFallback = "resources-fallback.eveonline.com";
    internal const string BinaryFallback = "binaries-fallback.eveonline.com";
    // Keep the original four indices for legacy JSON handling and existing reports.
    internal static readonly string[] All = [Resources, Binaries, "launcher.eveonline.com", "launcher.ccpgames.com", ResourceFallback, BinaryFallback];
    internal static readonly string[] Downloads = [BinaryFallback, ResourceFallback, Binaries, Resources, "launcher.ccpgames.com"];
    internal static readonly string[][] Families = [[Resources, ResourceFallback], [Binaries, BinaryFallback]];

    internal static void MergeFamilyCandidates(Dictionary<string, Dictionary<string, string>> cache)
    {
        foreach (var family in Families)
            foreach (var domain in family)
                foreach (var sibling in family.Where(d => d != domain))
                    foreach (var pair in cache[sibling].ToArray())
                        if (pair.Value.Contains("系统") || pair.Value.Contains("用户补充"))
                        {
                            const string marker = "关联域名 hosts / 补充候选，须本域独立验证";
                            if (!cache[domain].TryGetValue(pair.Key, out var source)) cache[domain][pair.Key] = marker;
                            else if (!source.Contains("关联域名 hosts")) cache[domain][pair.Key] = source + " / " + marker;
                        }
    }

    internal static string Rehost(string url, string domain)
    {
        var source = new Uri(url);
        if (!All.Contains(source.Host) || !All.Contains(domain) || source.Scheme != "https" || !source.IsDefaultPort || source.UserInfo.Length != 0)
            throw new ArgumentException("只能为已支持的 EVE HTTPS 下载地址更换域名。");
        return new UriBuilder(source) { Host = domain, Query = "", Fragment = "" }.Uri.AbsoluteUri;
    }

    internal static string ProxyRules => "# 将下方的 EVE代理 替换成你自己的代理组名称。\r\n# 放在 GEOIP/CN/DIRECT、MATCH 等兜底规则之前。\r\n" +
        string.Join("\r\n", Downloads.Select(d => "DOMAIN," + d + ",EVE代理")) +
        "\r\nDOMAIN,launcher.eveonline.com,EVE代理\r\n\r\n" +
        "新版优先下载域名：binaries-fallback.eveonline.com、resources-fallback.eveonline.com。\r\n" +
        "启动器下载器可能直接建立连接。仅打开系统 HTTP 代理不一定能接管它；可使用支持进程/域名分流的 TUN/VPN 模式。\r\n" +
        "本助手测速使用直连；VPN 路线请在 VPN 客户端和启动器中验证。此文件不会更改系统代理或 hosts。\r\n";
}
