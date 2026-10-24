using System.Text;
using System.Text.RegularExpressions;

namespace EveUpdateHelper;

internal static class LauncherLogInspector
{
    internal sealed record Findings(List<string> Hosts, Dictionary<string, List<string>> Files)
    {
        internal List<string> Progress { get; init; } = [];
        internal string? Build { get; init; }
    }
    internal static Findings Parse(string text)
    {
        var hosts = new HashSet<string>();
        var files = NetworkScanner.DownloadDomains.ToDictionary(d => d, _ => new List<string>());
        var progress = new List<string>();
        string? build = null;
        foreach (var line in text.Split('\n'))
        {
            var buildMatch = Regex.Match(line, @"\[server-manager\]\s+Starting download of build (\d{1,12})\b");
            if (buildMatch.Success) build = buildMatch.Groups[1].Value;
            var status = Regex.Match(line, @"\[server-manager\]\s+(Downloaded [\d.]+ (?:[kKMGT]iB|B)/[\d.]+ (?:[kKMGT]iB|B) at [\d.]+ (?:[kKMGT]iB|B)/s|Downloading \d+ (?:binary|resource) files \([\d.]+ [A-Za-z]+\))");
            if (status.Success) progress.Add(status.Groups[1].Value);
            foreach (Match match in Regex.Matches(line, "https?://[^\\s\"'<>\\\\]+"))
            {
                if (!Uri.TryCreate(match.Value.TrimEnd(')', ',', ';'), UriKind.Absolute, out var url)) continue;
                if (NetworkScanner.Domains.Contains(url.Host) || line.Contains("download", StringComparison.OrdinalIgnoreCase)) hosts.Add(url.Host);
                if (url.Scheme == "https" && files.TryGetValue(url.Host, out var paths) &&
                    Regex.IsMatch(url.AbsolutePath, @"^/[a-fA-F0-9]{2}/[a-fA-F0-9_]+$") && url.IsDefaultPort && url.UserInfo.Length == 0)
                {
                    // Discard query strings and never export raw account or authentication log lines.
                    string safeUrl = "https://" + url.Host + url.AbsolutePath;
                    if (!paths.Contains(safeUrl)) paths.Add(safeUrl);
                }
            }
        }
        return new Findings(hosts.ToList(), files) { Progress = progress.TakeLast(6).ToList(), Build = build };
    }

    internal static Findings ReadRecent()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EVE Online", "Logs");
        var text = new StringBuilder();
        if (Directory.Exists(path))
            foreach (var file in Directory.GetFiles(path, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(2).Reverse())
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    stream.Seek(Math.Max(0, stream.Length - 512 * 1024), SeekOrigin.Begin);
                    using var reader = new StreamReader(stream);
                    text.AppendLine(reader.ReadToEnd());
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        return Parse(text.ToString());
    }
}
