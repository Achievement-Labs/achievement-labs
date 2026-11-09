#if LABS_UPDATE_TESTING
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace AchievementLabs.Desktop;

// Compiled only into private validation builds. No outgoing network requests;
// the normal updater still validates canonical URLs, hashes, versions and bytes.
internal sealed class UpdateTestTransport : HttpMessageHandler
{
    public static HttpClient CreateClient() => new(new UpdateTestTransport()) { Timeout = TimeSpan.FromMinutes(10) };
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Environment.GetEnvironmentVariable("LABS_UPDATE_TEST_DIRECTORY");
        if (string.IsNullOrWhiteSpace(root)) return Reply(HttpStatusCode.ServiceUnavailable, "Launch this private test build with Launch-Test.ps1.");
        if (request.Method != HttpMethod.Get || request.Headers.Authorization != null || request.Headers.Contains("tickets"))
            return Reply(HttpStatusCode.BadRequest, "Only public fixture GET requests are allowed.");
        var stage = File.ReadAllText(Path.Combine(root, "active.txt")).Trim();
        if (stage is not ("baseline" or "catalog" or "app" or "bad-catalog")) return Reply(HttpStatusCode.BadRequest, "Invalid test stage.");
        var uri = request.RequestUri!.AbsoluteUri;
        string relative;
        if (uri == "https://api.github.com/repos/Achievement-Labs/achievement-labs/releases?per_page=100") relative = "releases.json";
        else
        {
            var match = Regex.Match(uri, @"^https://github\.com/Achievement-Labs/achievement-labs/releases/download/(v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)/(update\.json|AchievementLabs\.exe|Events-Indexed\.zip)$");
            if (!match.Success) return Reply(HttpStatusCode.Forbidden, "The private update build cannot use another feed.");
            relative = Path.Combine(match.Groups[1].Value, match.Groups[2].Value);
        }
        var path = Path.Combine(root, stage, relative);
        if (!File.Exists(path) && relative != "releases.json" && !relative.EndsWith("Events-Indexed.zip"))
            path = Path.Combine(root, "assets", relative);
        if (!File.Exists(path)) return Reply(HttpStatusCode.NotFound, "Test fixture is missing.");
        var content = new StreamContent(File.OpenRead(path));
        content.Headers.ContentLength = new FileInfo(path).Length;
        content.Headers.ContentType = new MediaTypeHeaderValue(path.EndsWith(".json") ? "application/json" : "application/octet-stream");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
    private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string message)
        => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(message) });
}
#endif
