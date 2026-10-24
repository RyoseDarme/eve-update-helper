namespace EveUpdateHelper;

internal static class CandidatePlanner
{
    internal static string[] ForHttps(IEnumerable<TcpSample> samples, IEnumerable<string> preferred, int limit)
    {
        var sorted = samples.Where(s => s.Received > 0).OrderByDescending(s => (double)s.Received / s.Sent).ThenBy(s => s.AverageMs).ToArray();
        if (sorted.Length == 0 || limit <= 0) return [];
        var available = sorted.ToDictionary(s => s.IP);
        var result = new List<string>(); var used = new HashSet<string>();
        void Add(string ip) { if (result.Count < limit && available.ContainsKey(ip) && used.Add(ip)) result.Add(ip); }
        foreach (var ip in preferred) Add(ip);
        foreach (var sample in sorted.Take(Math.Max(0, (limit - result.Count) / 2))) Add(sample.IP);
        var groups = sorted.GroupBy(s => string.Join('.', s.IP.Split('.').Take(2))).Select(g => g.ToArray()).ToArray();
        for (int round = 0; result.Count < limit && round < groups.Max(g => g.Length); round++)
            foreach (var group in groups) if (round < group.Length) Add(group[round].IP);
        return result.ToArray();
    }
    internal static NodeResult[] AcrossRegions(IEnumerable<NodeResult> nodes)
    {
        var sorted = nodes.OrderBy(n => n.LatencyMs > 0 ? n.LatencyMs : n.TcpLatencyMs).ToArray();
        var groups = sorted.GroupBy(n => n.Region.Length >= 3 ? n.Region[..3] : string.Join('.', n.IP.Split('.').Take(2)))
            .Select(g => g.ToArray()).ToArray();
        var result = new List<NodeResult>();
        int length = groups.Length > 0 ? groups.Max(g => g.Length) : 0;
        for (int round = 0; round < length; round++) foreach (var group in groups) if (round < group.Length) result.Add(group[round]);
        return result.ToArray();
    }
    internal static NodeResult[] Verification(IEnumerable<SurveySample> survey, IEnumerable<NodeResult> https, bool precise)
    {
        var ranked = FastScreening.Rank(survey);
        var pool = https.ToArray();
        var baseline = Baseline(pool);
        var first = (baseline == null ? Array.Empty<NodeResult>() : new[] { baseline })
            .Concat(pool.Where(n => n.Source.Contains("关联域名 hosts")).OrderBy(n => n.LatencyMs).Take(1)).ToArray();
        if (!precise) return first.Concat(ranked).DistinctBy(n => n.IP).ToArray();
        var representatives = AcrossRegions(pool).Take(12);
        return first.Concat(ranked.Take(12)).Concat(representatives).Concat(ranked).DistinctBy(n => n.IP).Take(24).ToArray();
    }
    static NodeResult? Baseline(IEnumerable<NodeResult> pool) => pool.Where(n => n.Source.Contains("用户补充") || n.Source.Contains("系统"))
        .OrderByDescending(n => n.Source.Contains("用户补充")).ThenBy(n => n.LatencyMs).FirstOrDefault();
    internal static NodeResult[] Quick(IEnumerable<NodeResult> https)
    {
        var pool = https.ToArray(); var baseline = Baseline(pool);
        if (baseline == null) return AcrossRegions(pool).Take(3).ToArray();
        string area = baseline.Region.Length >= 3 ? baseline.Region[..3] : "";
        var otherRegions = AcrossRegions(pool.Where(n => n.Region.Length < 3 || n.Region[..3] != area));
        return new[] { baseline }.Concat(pool.Where(n => n.Source.Contains("关联域名 hosts")).OrderBy(n => n.LatencyMs).Take(1))
            .Concat(otherRegions).Concat(AcrossRegions(pool)).DistinctBy(n => n.IP).Take(3).ToArray();
    }
}
