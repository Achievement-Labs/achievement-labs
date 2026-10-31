using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Desktop.Platforms;

internal static class AccountLibraryChecks
{
    internal static async Task RunAsync(string output)
    {
        await CheckRunningLauncherValidationAsync();
        using (var attachment = new AccountLibraryClient(new GalaxyAttachmentHandler()))
        {
            var launcher = LauncherSession.FromGalaxyValues("fictional-refresh-session", "101", "Fixture Galaxy");
            var attached = await attachment.AttachGalaxyAsync(launcher, default);
            if (attached.AccountId != "101" || attached.SessionSource != "GOG Galaxy" || attached.DisplayName != "Fixture Galaxy") throw new Exception("Galaxy session attachment failed");
            var mismatchRejected = false;
            try { await attachment.AttachGalaxyAsync(launcher with { AccountId = "202" }, default); }
            catch (InvalidDataException) { mismatchRejected = true; }
            if (!mismatchRejected) throw new Exception("Galaxy attachment accepted a different account");
        }
        foreach (var values in new[] { ("", "101"), ("fixture", "../account"), ("fixture\nsecret", "101") })
        {
            var invalidRejected = false;
            try { LauncherSession.FromGalaxyValues(values.Item1, values.Item2, "Fixture"); }
            catch (InvalidOperationException) { invalidRejected = true; }
            if (!invalidRejected) throw new Exception("Invalid launcher session accepted");
        }
        using var client = new AccountLibraryClient(new FixtureHandler());
        foreach (var platform in new[] { "Epic", "GOG", "EA", "Ubisoft" })
        {
            var session = new ConnectedPlatformAccount(platform, "fixture-account", "Fixture user", "fixture-token", ExpiresAt: DateTimeOffset.UtcNow.AddHours(1));
            var result = await client.ReadAsync(session, default);
            if (result.Games.Length != 2 || result.Games.Any(g => g.AccountId != session.AccountId || g.ImageUrl.Length == 0 || Directory.Exists(g.InstallPath))) throw new Exception(platform + " did not load both uninstalled account titles with artwork");
            var unwanted = new PlatformGameRow(platform, "unowned-installed", "Unowned installed title", output, Rows: [new("WRONG", "Wrong state", "", true)], CatalogComplete: true, AccountId: "other-account");
            var wrongAccount = unwanted with { Id = result.Games[0].Id };
            var enriched = AccountLibraryClient.MergeInstallations(result, [unwanted, wrongAccount], session.AccountId);
            if (enriched.Games.Length != 2 || enriched.Games.Any(g => g.Id == unwanted.Id || g.Earned != 0 || g.IsComplete)) throw new Exception("Installation metadata changed ownership or leaked another account's achievements");
        }
        var epic = new ConnectedPlatformAccount("Epic", "fixture-account", "Fixture", "fixture-token");
        var rejected = false;
        try { await client.CursorPagesAsync("https://attacker.invalid/items", epic, "records", "next", "cursor", default); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception("Credentials were allowed onto an unrelated host");
        using var looping = new AccountLibraryClient(new FixtureHandler(loop: true));
        rejected = false;
        try { await looping.CursorPagesAsync("https://library-service.live.use1a.on.epicgames.com/library/api/public/items", epic, "records", "responseMetadata.nextCursor", "cursor", default); } catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Repeated cursor silently yielded a partial library");
        if (AccountLibraryClient.NormalizeImage("//images.gog.com/cover_{formatter}.{ext}") != "https://images.gog.com/cover_.jpg" || AccountLibraryClient.NormalizeImage("javascript:alert(1)").Length != 0) throw new Exception("Artwork URL normalization");
        if (PlatformBrowserLogin.IsLoginHost("Epic", "https://www.epicgames.com.attacker.invalid/id/api/redirect") || PlatformBrowserLogin.IsLoginHost("GOG", "https://user:secret@embed.gog.com/on_login_success") || PlatformBrowserLogin.IsLoginHost("EA", "http://accounts.ea.com/connect/auth")) throw new Exception("Sign-in origin validation");
        foreach (var imageFile in new[] { "src/AchievementLabs.App/Assets/achievement-labs-icon.png", "Steam_icon_logo.svg.webp", "images.jpg" })
        {
            using var images = new HttpClient(new ImageHandler(File.ReadAllBytes(imageFile)));
            using var bitmap = await RemoteImage.FetchBitmapAsync(images, new Uri("https://fixture.invalid/cover"));
            if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0) throw new Exception("Downloaded PNG/WebP artwork did not decode");
        }
        File.WriteAllText(Path.Combine(output, "account-library-checks.txt"), "PASS: Galaxy launcher attachment without a replacement session; mismatched identities and malformed sessions rejected; authenticated Epic/GOG paginated libraries, EA entitlements and Ubisoft ownership; uninstalled games retained; DLC and other-platform entries filtered; cover metadata normalized; local installations cannot add unowned games or another account's progress; repeated cursors and credential forwarding rejected; sign-in origin checks. Mock sessions only; no real account tokens or sign-in operations.");
    }
    private static async Task CheckRunningLauncherValidationAsync()
    {
        var yaml = "root:\n  name: GAMENAME\n  thumb_image: THUMB\n  start_game:\n    online: {}\nlocalizations:\n  default:\n    GAMENAME: Fixture title\n    THUMB: fixture-cover.jpg\n";
        byte[] Owned(ulong id, ulong type, ulong owned) => Proto.Message(1, Proto.Join(Proto.Number(1, id), Proto.Number(19, id), Proto.Number(8, type), Proto.Number(20, owned), Proto.Message(17, Encoding.UTF8.GetBytes(yaml))));
        var wire = Proto.Message(1, Proto.Join(Proto.Number(1, 1), Proto.Message(2, Proto.Join(Proto.Number(1, 1), Proto.Message(2, Proto.Join(Owned(100, 0, 1), Owned(200, 0, 0), Owned(300, 1, 1)))))));
        var library = UbisoftOwnershipClient.ParseResponse(wire, "fixture-account");
        if (library.Length != 1 || library[0].Id != "100" || library[0].Name != "Fixture title" || !library[0].ImageUrl.EndsWith("fixture-cover.jpg") || library[0].AccountId != "fixture-account") throw new Exception("Ubisoft ownership wire parsing leaked unowned/addon products or lost metadata");
        var truncatedRejected = false;
        try { UbisoftOwnershipClient.ParseResponse(wire[..^1], "fixture-account"); } catch (InvalidDataException) { truncatedRejected = true; }
        if (!truncatedRejected) throw new Exception("Partial Ubisoft ownership response accepted");
        using var client = new AccountLibraryClient(new LauncherFixtureHandler());
        var candidate = new ConnectedPlatformAccount("EA", "", "", "fixture-first", SessionSource: "EA app");
        var session = await LauncherSession.ValidateCandidatesAsync([candidate with { AccessToken = "stale" }, candidate], client, default);
        if (session.AccountId != "first" || session.SessionSource != "EA app") throw new Exception("Stale launcher candidate hid the verified session");
        var rejected = false;
        try { await LauncherSession.ValidateCandidatesAsync([candidate, candidate with { AccessToken = "fixture-second" }], client, default); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception("Multiple launcher accounts were implicitly selected");
        var expired = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":1,\"userId\":\"fixture\"}"));
        if (RunningLauncherSession.ParseCandidate("EA", "eyJhbGciOiJub25lIn0." + expired + ".fixture-signature") != null) throw new Exception("Expired launcher token accepted");
        var foreign = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"iss\":\"https://accounts.google.com\",\"exp\":4102444800}"));
        if (RunningLauncherSession.ParseCandidate("EA", "eyJhbGciOiJub25lIn0." + foreign + ".fixture-signature") != null || RunningLauncherSession.ParseCandidate("Epic", "eyJhbGciOiJub25lIn0." + foreign + ".fixture-signature") != null) throw new Exception("Linked third-party identity token accepted for another provider");
        if (RunningLauncherSession.TrustedPath("EA", @"C:\unrelated\EADesktop.exe") || RunningLauncherSession.TrustedPath("EA", @"C:\EA Desktop\other.exe")) throw new Exception("Unrelated executable accepted as launcher");
    }
    private sealed class LauncherFixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.Authorization?.Parameter;
            if (token == "stale") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (request.RequestUri?.Host != "service-aggregation-layer.juno.ea.com") throw new Exception("Launcher validation sent credentials outside the provider");
            var id = token == "fixture-first" ? "first" : "second";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JObject { ["data"] = new JObject { ["me"] = new JObject { ["player"] = new JObject { ["pd"] = id, ["psd"] = "fixture-persona", ["displayName"] = "Fixture" } } } }.ToString(), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class GalaxyAttachmentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string json;
            if (uri.Host == "auth.gog.com" && uri.AbsolutePath == "/token")
            {
                if (!uri.Query.Contains("without_new_session=1") || !uri.Query.Contains("grant_type=refresh_token")) throw new Exception("Attachment attempted to replace the Galaxy session");
                json = "{\"user_id\":\"101\",\"access_token\":\"fictional-access\",\"refresh_token\":\"fictional-refresh\",\"expires_in\":3600}";
            }
            else if (uri.Host == "users.gog.com" && uri.AbsolutePath == "/users/101" && request.Headers.Authorization?.Parameter == "fictional-access")
                json = "{\"id\":\"101\",\"username\":\"Fixture Galaxy\"}";
            else throw new Exception("Unexpected Galaxy attachment request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class ImageHandler(byte[] image) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
    }
    private sealed class FixtureHandler(bool loop = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            JToken response;
            if (uri.Host == "library-service.live.use1a.on.epicgames.com")
            {
                var second = uri.Query.Contains("cursor=");
                response = new JObject { ["records"] = new JArray(new JObject { ["catalogItemId"] = second ? "epic-two" : "epic-one", ["namespace"] = "fixture", ["appName"] = second ? "two" : "one", ["metadata"] = new JObject { ["title"] = second ? "Uninstalled game two" : "Uninstalled game one", ["keyImages"] = new JArray(new JObject { ["type"] = "DieselGameBoxTall", ["url"] = "https://cdn.epicgames.com/fixture.jpg" }) } }), ["responseMetadata"] = new JObject { ["nextCursor"] = second && !loop ? null : "next fixture + encoded" } };
            }
            else if (uri.Host == "galaxy-library.gog.com")
            {
                var second = uri.Query.Contains("page_token=");
                response = new JObject { ["items"] = new JArray(new JObject { ["platform_id"] = "gog", ["external_id"] = second ? "gog-two" : "gog-one" }, new JObject { ["platform_id"] = "steam", ["external_id"] = "not-a-gog-title" }), ["next_page_token"] = second ? null : "page two" };
            }
            else if (uri.Host == "gamesdb.gog.com")
                response = new JObject { ["type"] = "game", ["title"] = new JObject { ["*"] = "Uninstalled game " + uri.Segments.Last() }, ["game"] = new JObject { ["vertical_cover"] = new JObject { ["url_format"] = "https://images.gog.com/fixture{formatter}.{ext}" } } };
            else if (uri.Host == "service-aggregation-layer.juno.ea.com")
            {
                var query = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (query["query"]!.ToString().Contains("ownedGameProducts"))
                {
                    var second = AccountLibraryClient.Text(query["variables"], "next").Length > 0;
                    response = new JObject { ["data"] = new JObject { ["me"] = new JObject { ["ownedGameProducts"] = new JObject { ["next"] = second ? null : "fixture-next", ["items"] = new JArray(new JObject { ["originOfferId"] = second ? "ea-two" : "ea-one", ["product"] = new JObject { ["baseItem"] = new JObject { ["gameType"] = "BASE_GAME" } } }, new JObject { ["originOfferId"] = "missing-product", ["product"] = null }) } } } };
                }
                else response = new JObject { ["data"] = new JObject { ["legacyOffers"] = new JArray(new JObject { ["offerId"] = "ea-one", ["contentId"] = "content-one" }, new JObject { ["offerId"] = "ea-two", ["contentId"] = "content-two" }), ["gameProducts"] = new JObject { ["items"] = new JArray(new[] { "ea-one", "ea-two" }.Select(id => new JObject { ["id"] = id, ["originOfferId"] = id, ["baseItem"] = new JObject { ["title"] = "Uninstalled EA game", ["packArt"] = new JObject { ["largestImage"] = new JObject { ["path"] = "https://eaassets-a.akamaihd.net/fixture.jpg" } } } })) } } };
            }
            else if (uri.AbsolutePath.Contains("consolidatedentitlements"))
                response = new JObject { ["entitlements"] = new JArray(new JObject { ["offerType"] = "basegame", ["offerId"] = "ea-one" }, new JObject { ["offerType"] = "basegame", ["offerId"] = "ea-two" }, new JObject { ["offerType"] = "dlc", ["offerId"] = "not-a-base-game" }) };
            else if (uri.AbsolutePath.Contains("/supercat/"))
                response = new JObject { ["i18n"] = new JObject { ["displayName"] = "Uninstalled EA game", ["packArtLarge"] = "https://eaassets-a.akamaihd.net/fixture.jpg" } };
            else if (uri.Host == "public-ubiservices.ubi.com")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (!body.Contains("isOwned:true")) throw new Exception("Ubisoft query did not request ownership");
                response = new JObject { ["data"] = new JObject { ["viewer"] = new JObject { ["games"] = new JObject { ["totalCount"] = 2, ["nodes"] = new JArray(new JObject { ["id"] = "ubi-one", ["spaceId"] = "space-one", ["name"] = "Uninstalled Ubisoft one", ["lowBoxArtUrl"] = "https://ubistatic3-a.akamaihd.net/one.jpg" }, new JObject { ["id"] = "ubi-two", ["spaceId"] = "space-two", ["name"] = "Uninstalled Ubisoft two", ["lowBoxArtUrl"] = "https://ubistatic3-a.akamaihd.net/two.jpg" }) } } } };
            }
            else throw new Exception("Unexpected account fixture route: " + uri.Host);
            if (request.Headers.Authorization == null && !request.Headers.Contains("Authorization")) throw new Exception("Account request has no authorization");
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToString(), Encoding.UTF8, "application/json") };
        }
    }
}
