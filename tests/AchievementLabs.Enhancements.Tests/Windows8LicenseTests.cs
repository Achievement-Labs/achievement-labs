using System.Net;
using System.Text.Json;
using System.Xml;
using AchievementLabs.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;

public static class Windows8LicenseTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    sealed class ProfileHandler(string xuid, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellation)
        {
            Assert(message.Method == HttpMethod.Get && message.RequestUri!.AbsoluteUri == "https://profile.xboxlive.com/users/me/id", "Only a read-only profile request is allowed");
            Assert(message.Headers.GetValues("Authorization").Single().StartsWith("XBL2.0 x="), "Legacy authorization is preserved");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(new { xuid })) });
        }
    }
    public static void Run()
    {
        var auth = Windows8LicenseClient.BuildAuthorization("<jwt v=\"1\" t=\"synthetic-token\" />");
        Assert(auth.StartsWith("XBL2.0 x=<jwt") && auth.Contains("synthetic-token"), "Service token becomes a legacy header");
        foreach (var invalid in new[] { "<Receipt />", "<jwt v=\"1\" />", "<!DOCTYPE jwt [<!ENTITY t SYSTEM 'file:///missing'>]><jwt t=\"&t;\" />" })
        {
            var rejected = false;
            try { Windows8LicenseClient.BuildAuthorization(invalid); } catch (Exception e) when (e is InvalidDataException or XmlException) { rejected = true; }
            Assert(rejected, "Receipts, missing token values and XML entities are rejected");
        }
        using (var http = new HttpClient(new ProfileHandler("123"))) Windows8LicenseClient.VerifyAccountAsync(http, auth, "123", default).GetAwaiter().GetResult();
        foreach (var handler in new[] { new ProfileHandler("456"), new ProfileHandler("123", HttpStatusCode.Unauthorized) })
        {
            using var http = new HttpClient(handler); var rejected = false;
            try { Windows8LicenseClient.VerifyAccountAsync(http, auth, "123", default).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Wrong account and rejected token cannot become ready");
        }
        var root = Path.Combine(AppContext.BaseDirectory, "vendor", "gfw8-license-grabber", "backend");
        using var games = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "games.json")));
        using var identities = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "runtime", "jetpack-auth-investigation", "shared-auth-helper", "identities.json")));
        var configs = identities.RootElement.EnumerateArray().ToDictionary(x => x.GetProperty("gameId").GetInt32());
        var titles = new HashSet<uint>();
        foreach (var game in games.RootElement.GetProperty("games").EnumerateArray())
        {
            var config = configs[game.GetProperty("index").GetInt32()];
            var id = game.GetProperty("legacy_xbox_title_id").GetUInt32();
            Assert(titles.Add(id), "Each route has a unique title ID");
            Assert(config.GetProperty("titleId").GetUInt32() == id && config.GetProperty("family").GetString() == game.GetProperty("package_family_name").GetString(), "Catalog and native helper match the exact title and package");
        }
        Assert(titles.Count == 108, "All 108 upstream routes are packaged");
        AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
        AppBuilder.Configure<AchievementLabs.Desktop.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var window = new MainWindow();
        var buttons = window.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string).ToArray();
        Assert(buttons.Contains("Launch game") && buttons.Contains("Grab token") && buttons.Contains("Get license & token"), "Both acquisition paths exist in the compiled UI");
        Assert(!buttons.Contains("Prepare request"), "Preparation button is absent");
        window.Close();
        Console.WriteLine("PASS: token XML, account match/rejection, 108 exact routes, and compiled UI with both acquisition paths. All HTTP mocked; no licenses, tokens or unlocks requested.");
    }
}
