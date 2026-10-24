using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EveUpdateHelper;

internal sealed record ReleaseInfo(string Version, string Notes, string PageUrl);
internal sealed record UpdateResult(ReleaseInfo? Release, string Message);

internal static class UpdateChecker
{
    internal const int MaximumBytes = 512 * 1024;
    internal static string ValidateRepository(string repository)
    {
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]{1,100}$") ||
            repository.Split('/')[1] is "." or "..")
            throw new ArgumentException("GitHub 仓库地址无效。");
        return repository;
    }

    internal static Version StableVersion(string text)
    {
        if (!Regex.IsMatch(text, @"^[vV]?\d{1,6}\.\d{1,6}(?:\.\d{1,6}){0,2}$"))
            throw new IOException("版本号不符合正式版格式。");
        var v = System.Version.Parse(text.TrimStart('v', 'V'));
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    internal static ReleaseInfo? Parse(string json, string repository, string currentVersion)
    {
        ValidateRepository(repository);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = StableVersion(tag);
        if (version <= StableVersion(currentVersion)) return null;
        var page = root.GetProperty("html_url").GetString() ?? "";
        if (!Uri.TryCreate(page, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith("/" + repository + "/releases/tag/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Length <= repository.Length + "/releases/tag/".Length + 1)
            throw new IOException("更新地址不是本项目的 GitHub 发布页。");
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
        notes = new string(notes.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').Take(6000).ToArray());
        return new ReleaseInfo(tag.TrimStart('v', 'V'), notes, uri.AbsoluteUri);
    }

    internal static async Task<UpdateResult> CheckAsync(string repository, string currentVersion, CancellationToken ct,
        HttpClient? testClient = null)
    {
        if (string.IsNullOrWhiteSpace(repository)) return new(null, "尚未设置官方更新仓库。");
        ValidateRepository(repository);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var ownedClient = testClient == null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) : null;
        var client = testClient ?? ownedClient!;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("EveUpdateHelper/" + AppInfo.Version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(null, "仓库尚无正式版本，或仓库暂不可访问。");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return new(null, "GitHub 暂时限制检查频率，请稍后重试。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new IOException("更新信息超过大小限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new IOException("更新信息超过大小限制。");
            buffer.Write(chunk, 0, count);
        }
        var release = Parse(Encoding.UTF8.GetString(buffer.ToArray()), repository, currentVersion);
        return new(release, release == null ? $"当前 v{currentVersion} 已是最新正式版。" : $"发现新版本 v{release.Version}");
    }
}

internal sealed class UpdatePreferences
{
    public bool AutomaticChecks { get; set; } = true;
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EveUpdateHelper", "update-settings.json");
    internal static UpdatePreferences Load()
    {
        try { return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch { return new(); }
    }
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
    }
}
