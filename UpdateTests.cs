using System.Net;
using System.Text;
using System.Text.Json;

namespace EveUpdateHelper;

internal static class UpdateTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string repository = "fixture/eve-update-helper";
        string Json(string tag = "v2.4.0", string? url = null, bool draft = false, bool prerelease = false) =>
            JsonSerializer.Serialize(new { tag_name = tag, html_url = url ?? "https://github.com/fixture/eve-update-helper/releases/tag/" + tag,
                draft, prerelease, body = "更新说明\n支持更新提醒\u0001" });
        bool Reject(Action action)
        {
            try { action(); return false; }
            catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or InvalidOperationException or KeyNotFoundException) { return true; }
        }
        var release = UpdateChecker.Parse(Json(), repository, "2.3.0");
        check(release?.Version == "2.4.0" && release.Notes.Contains("更新说明") && !release.Notes.Contains('\u0001'),
            "updates preserve release notes and remove control characters");
        check(UpdateChecker.Parse(Json("v2.3.0"), repository, "2.3.0") == null, "equal release is not an update");
        check(UpdateChecker.Parse(Json("2.3"), repository, "2.3.0") == null, "version normalization treats 2.3 as 2.3.0");
        check(UpdateChecker.Parse(Json("v2.2.9"), repository, "2.3.0") == null, "older release never prompts a downgrade");
        check(UpdateChecker.Parse(Json("v2.10.0"), repository, "2.9.0") != null, "versions compare numerically rather than lexically");
        check(UpdateChecker.Parse(Json(draft: true), repository, "2.3.0") == null &&
            UpdateChecker.Parse(Json(prerelease: true), repository, "2.3.0") == null, "draft and prerelease releases are excluded");
        check(Reject(() => UpdateChecker.Parse(Json("v2.4.0-beta"), repository, "2.3.0")), "nonstable release tag is rejected");
        foreach (var url in new[] { "http://github.com/fixture/eve-update-helper/releases/tag/v2.4.0",
            "https://github.com.evil.example/fixture/eve-update-helper/releases/tag/v2.4.0",
            "https://evil.example/fixture/eve-update-helper/releases/tag/v2.4.0",
            "https://github.com/other/project/releases/tag/v2.4.0", "file:///C:/Windows/notepad.exe",
            "https://user@github.com/fixture/eve-update-helper/releases/tag/v2.4.0",
            "https://github.com:444/fixture/eve-update-helper/releases/tag/v2.4.0" })
            check(Reject(() => UpdateChecker.Parse(Json(url: url), repository, "2.3.0")), "unsafe or unrelated release URL rejected: " + url);
        check(Reject(() => UpdateChecker.ValidateRepository("owner/repo/../../evil")) &&
            Reject(() => UpdateChecker.ValidateRepository("https://github.com/owner/repo")), "repository cannot alter GitHub API path");
        check(Reject(() => UpdateChecker.Parse("not json", repository, "2.3.0")) &&
            Reject(() => UpdateChecker.Parse("{}", repository, "2.3.0")), "invalid release responses do not become available updates");

        using (var client = new HttpClient(new Handler((request, _) =>
        {
            check(request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/fixture/eve-update-helper/releases/latest" &&
                request.Headers.UserAgent.ToString().Contains("EveUpdateHelper/"), "update request uses selected public repository and application user agent");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json()) });
        })))
        {
            var result = UpdateChecker.CheckAsync(repository, "2.3.0", CancellationToken.None, client).GetAwaiter().GetResult();
            check(result.Release?.Version == "2.4.0", "HTTP response reaches release parser and exposes newer version");
        }
        foreach (var code in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(code))));
            var result = UpdateChecker.CheckAsync(repository, "2.3.0", CancellationToken.None, client).GetAwaiter().GetResult();
            check(result.Release == null && !result.Message.Contains("已是最新"), "missing repository or rate limit is not reported as up to date: " + code);
        }
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new string('x', UpdateChecker.MaximumBytes + 1)) }))))
            check(Reject(() => UpdateChecker.CheckAsync(repository, "2.3.0", CancellationToken.None, client).GetAwaiter().GetResult()),
                "oversized release content length is rejected");
        using (var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new UnboundedContent(new string('x', UpdateChecker.MaximumBytes + 1)) }))))
            check(Reject(() => UpdateChecker.CheckAsync(repository, "2.3.0", CancellationToken.None, client).GetAwaiter().GetResult()),
                "oversized update stream is bounded when content length is unknown");
        using (var stop = new CancellationTokenSource())
        using (var client = new HttpClient(new Handler(async (_, ct) =>
        {
            stop.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })))
        {
            bool canceled = false;
            try { UpdateChecker.CheckAsync(repository, "2.3.0", stop.Token, client).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled, "update HTTP request stops when measurement starts or window closes");
        }
    }

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
    sealed class UnboundedContent(string value) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();
    }
}
