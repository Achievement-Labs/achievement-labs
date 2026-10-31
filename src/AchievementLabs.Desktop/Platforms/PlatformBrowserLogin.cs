using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace AchievementLabs.Desktop.Platforms;

// A dedicated browser profile and random loopback debugging port belong solely to this sign-in.
// No existing browser profile, process memory or unrelated tabs are inspected.
internal sealed class PlatformBrowserLogin : IAsyncDisposable
{
    private readonly string platform;
    private readonly string state = Guid.NewGuid().ToString("N");
    private readonly string profile = AchievementLabs.Core.AchievementLabsPaths.LocalFile(Path.Combine("platform-signin", Guid.NewGuid().ToString("N")));
    private readonly HttpClient local = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ClientWebSocket socket = new();
    private int commandId;
    private string? targetId;
    private int port;
    internal PlatformBrowserLogin(string platform) => this.platform = platform;
    internal async Task<ConnectedPlatformAccount> SignInAsync(CancellationToken token)
    {
        var executable = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Install Microsoft Edge or Chrome to sign in.");
        using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in new[] { "--user-data-dir=" + profile, "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + port, "--no-first-run", "--no-default-browser-check", "--disable-background-mode", "--new-window", StartUrl(platform, state) }) start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = deadline.Token;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var targets = JsonNode.Parse(await local.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct))!.AsArray();
                var target = targets.FirstOrDefault(t => t?["type"]?.ToString() == "page" && IsLoginHost(platform, t["url"]?.ToString() ?? ""));
                if (target != null)
                {
                    targetId = target["id"]!.ToString();
                    var uri = new Uri(target["webSocketDebuggerUrl"]!.ToString());
                    if (!uri.IsLoopback || uri.Port != port || uri.Scheme != "ws") throw new InvalidDataException("Unexpected sign-in browser endpoint.");
                    await socket.ConnectAsync(uri, ct); break;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(200, ct);
        }
        if (socket.State != WebSocketState.Open) throw new InvalidOperationException("The sign-in browser could not be connected. Close its window and retry.");
        var eaTokenStep = false;
        while (!ct.IsCancellationRequested)
        {
            var location = await EvaluateAsync("location.href", ct);
            if (!IsLoginHost(platform, location)) { await Task.Delay(400, ct); continue; }
            if (platform == "GOG" && Uri.TryCreate(location, UriKind.Absolute, out var gog) && gog.Host == "embed.gog.com" && gog.AbsolutePath == "/on_login_success")
            {
                var query = HttpUtility.ParseQueryString(gog.Query);
                if (query["state"] != state || string.IsNullOrWhiteSpace(query["code"])) throw new InvalidDataException("The GOG sign-in response could not be verified.");
                return await AccountLibraryClient.ExchangeCodeAsync(platform, query["code"]!, ct);
            }
            if (platform == "Epic" && Uri.TryCreate(location, UriKind.Absolute, out var epic) && epic.Host == "www.epicgames.com" && epic.AbsolutePath == "/id/api/redirect")
            {
                var body = await EvaluateAsync("document.body.innerText", ct);
                JsonNode? data = null; try { data = JsonNode.Parse(body); } catch { }
                var code = data?["authorizationCode"]?.ToString() ?? data?["code"]?.ToString();
                if (!string.IsNullOrWhiteSpace(code)) return await AccountLibraryClient.ExchangeCodeAsync(platform, code, ct);
            }
            if (platform == "EA")
            {
                if (!eaTokenStep && Uri.TryCreate(location, UriKind.Absolute, out var origin) && origin.Host == "www.origin.com" && origin.AbsolutePath == "/views/login.html")
                {
                    eaTokenStep = true;
                    await CommandAsync("Page.navigate", new { url = "https://accounts.ea.com/connect/auth?client_id=ORIGIN_JS_SDK&response_type=token&redirect_uri=nucleus%3Arest&prompt=none" }, ct);
                }
                else if (eaTokenStep && Uri.TryCreate(location, UriKind.Absolute, out var ea) && ea.Host == "accounts.ea.com" && ea.AbsolutePath == "/connect/auth")
                {
                    JsonNode? data = null; try { data = JsonNode.Parse(await EvaluateAsync("document.body.innerText", ct)); } catch { }
                    if (data?["access_token"]?.ToString() is { Length: > 0 } access)
                    {
                        var cookies = await CommandAsync("Network.getCookies", new { urls = new[] { "https://accounts.ea.com/", "https://ea.com/" } }, ct);
                        return await AccountLibraryClient.IdentifyAsync(new("EA", "", "", access, ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(int.TryParse(data["expires_in"]?.ToString(), out var seconds) ? seconds : 1800), Cookies: cookies?["cookies"]?.ToJsonString() ?? "[]"), ct);
                    }
                }
            }
            if (platform == "Ubisoft" && Uri.TryCreate(location, UriKind.Absolute, out var ubi) && ubi.Host == "connect.ubisoft.com" && ubi.AbsolutePath.StartsWith("/ready", StringComparison.Ordinal))
            {
                var values = JsonNode.Parse(await EvaluateAsync("JSON.stringify({login:localStorage.getItem('PRODloginData'),remember:localStorage.getItem('PRODrememberMe'),profile:localStorage.getItem('PRODlastProfile')})", ct));
                JsonObject merged = new();
                foreach (var key in new[] { "login", "remember", "profile" })
                {
                    if (values?[key]?.ToString() is not { Length: > 0 } text) continue;
                    try { if (JsonNode.Parse(text) is JsonObject obj) foreach (var pair in obj) merged[pair.Key] = pair.Value?.DeepClone(); } catch { }
                }
                if (merged["ticket"]?.ToString() is { Length: > 0 } ticket && merged["userId"]?.ToString() is { Length: > 0 } user)
                    return await AccountLibraryClient.IdentifyAsync(new("Ubisoft", user, merged["nameOnPlatform"]?.ToString() ?? user, ticket, merged["rememberMeTicket"]?.ToString() ?? "", DateTimeOffset.UtcNow.AddHours(1), merged["sessionId"]?.ToString() ?? ""), ct);
            }
            await Task.Delay(400, ct);
        }
        throw new OperationCanceledException(ct);
    }
    internal static string StartUrl(string platform, string state) => platform switch
    {
        "Epic" => "https://www.epicgames.com/id/login?redirectUrl=" + Uri.EscapeDataString("https://www.epicgames.com/id/api/redirect?clientId=" + AccountLibraryClient.EpicClientId + "&responseType=code"),
        "GOG" => "https://auth.gog.com/auth?client_id=" + AccountLibraryClient.GogClientId + "&redirect_uri=" + Uri.EscapeDataString(AccountLibraryClient.GogRedirect) + "&response_type=code&layout=client2&state=" + state,
        "EA" => "https://accounts.ea.com/connect/auth?response_type=code&client_id=ORIGIN_SPA_ID&display=originXWeb/login&locale=en_US&release_type=prod&redirect_uri=https%3A%2F%2Fwww.origin.com%2Fviews%2Flogin.html&state=" + state,
        "Ubisoft" => "https://connect.ubisoft.com/login?appId=" + AccountLibraryClient.UbisoftAppId + "&genomeId=fbd6791c-a6c6-4206-a75e-77234080b87b&lang=en-US&nextUrl=https%3A%2F%2Fconnect.ubisoft.com%2Fready",
        _ => throw new ArgumentException("Unknown platform.")
    };
    internal static bool IsLoginHost(string platform, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        return platform switch { "Epic" => uri.Host is "www.epicgames.com" or "accounts.epicgames.com", "GOG" => uri.Host is "auth.gog.com" or "login.gog.com" or "embed.gog.com", "EA" => uri.Host is "accounts.ea.com" or "signin.ea.com" or "www.origin.com", "Ubisoft" => uri.Host is "connect.ubisoft.com", _ => false };
    }
    private async Task<string> EvaluateAsync(string expression, CancellationToken ct) => (await CommandAsync("Runtime.evaluate", new { expression, returnByValue = true }, ct))?["result"]?["value"]?.ToString() ?? "";
    private async Task<JsonNode?> CommandAsync(string method, object parameters, CancellationToken ct)
    {
        var id = ++commandId;
        await socket.SendAsync(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { id, method, @params = parameters })), WebSocketMessageType.Text, true, ct);
        var buffer = new byte[16384];
        while (true)
        {
            using var data = new MemoryStream(); WebSocketReceiveResult received;
            do { received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct); if (received.MessageType == WebSocketMessageType.Close) throw new OperationCanceledException("The sign-in window was closed."); data.Write(buffer, 0, received.Count); if (data.Length > 4_194_304) throw new InvalidDataException("Sign-in response exceeded its limit."); } while (!received.EndOfMessage);
            var response = JsonNode.Parse(data.ToArray());
            if ((int?)response?["id"] != id) continue;
            if (response?["error"] != null) throw new InvalidOperationException("The sign-in page could not be read. Retry sign-in.");
            return response?["result"];
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (targetId != null) { try { await local.GetAsync($"http://127.0.0.1:{port}/json/close/{Uri.EscapeDataString(targetId)}"); } catch { } }
        socket.Dispose(); local.Dispose();
        var parent = Path.GetFullPath(AchievementLabs.Core.AchievementLabsPaths.LocalFile("platform-signin")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(profile).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return;
        for (var i = 0; i < 5; i++) { try { if (Directory.Exists(profile)) Directory.Delete(profile, true); return; } catch { await Task.Delay(200); } }
    }
}
