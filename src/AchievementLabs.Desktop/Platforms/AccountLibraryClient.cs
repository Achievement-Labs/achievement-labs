using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Desktop.Platforms;

// Protocol references: docs/account-library-integration.md. No gameplay writes or presence calls.
internal sealed partial class AccountLibraryClient : IDisposable
{
    internal const string EpicClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string EpicClientSecret = "daafbccc737745039dffe53d94fc76cf";
    internal const string GogClientId = "46899977096215655";
    private const string GogClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    internal const string GogRedirect = "https://embed.gog.com/on_login_success?origin=client";
    internal const string UbisoftAppId = "b8fde481-327d-4031-85ce-7c10a202a700";
    private readonly HttpClient http;
    internal AccountLibraryClient(HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementLabs/1.0");
    }
    public void Dispose() => http.Dispose();
    internal static async Task<ConnectedPlatformAccount> ExchangeCodeAsync(string platform, string code, CancellationToken ct)
    {
        using var client = new AccountLibraryClient();
        var fields = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code };
        var account = await client.TokenAsync(platform, fields, ct);
        return await IdentifyAsync(account, ct);
    }
    private async Task<ConnectedPlatformAccount> TokenAsync(string platform, Dictionary<string, string> fields, CancellationToken ct, ConnectedPlatformAccount? previous = null)
    {
        HttpRequestMessage request;
        if (platform == "Epic")
        {
            fields["token_type"] = "eg1";
            request = new(HttpMethod.Post, "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token") { Content = new FormUrlEncodedContent(fields) };
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(EpicClientId + ":" + EpicClientSecret)));
        }
        else if (platform == "GOG")
        {
            fields["client_id"] = GogClientId; fields["client_secret"] = GogClientSecret;
            if (fields["grant_type"] == "authorization_code") fields["redirect_uri"] = GogRedirect;
            request = new(HttpMethod.Get, "https://auth.gog.com/token?" + string.Join("&", fields.Select(kv => Escape(kv.Key) + "=" + Escape(kv.Value))));
        }
        else throw new InvalidOperationException("Unsupported token exchange.");
        var json = await SendAsync(request, ct);
        var token = Text(json, "access_token");
        if (token.Length == 0) throw new InvalidOperationException("Sign-in did not return an access token. Retry sign-in.");
        return new(platform, Text(json, platform == "GOG" ? "user_id" : "account_id"), Text(json, "displayName"), token,
            Text(json, "refresh_token") is { Length: > 0 } refresh ? refresh : previous?.RefreshToken ?? "",
            DateTimeOffset.UtcNow.AddSeconds((int?)json["expires_in"] ?? 1800));
    }
    internal static async Task<ConnectedPlatformAccount> IdentifyAsync(ConnectedPlatformAccount account, CancellationToken ct)
    {
        using var client = new AccountLibraryClient();
        return await client.IdentifyCoreAsync(account, ct);
    }
    internal async Task<ConnectedPlatformAccount> AttachGalaxyAsync(ConnectedPlatformAccount launcher, CancellationToken ct)
    {
        // Request a token within the existing session rather than creating a replacement launcher session.
        var session = await TokenAsync("GOG", new() { ["grant_type"] = "refresh_token", ["refresh_token"] = launcher.RefreshToken, ["without_new_session"] = "1" }, ct);
        if (session.AccountId != launcher.AccountId) throw new InvalidDataException("Galaxy account changed. Attach again after Galaxy finishes signing in.");
        session = await IdentifyCoreAsync(session, ct);
        return session with { SessionSource = "GOG Galaxy" };
    }
    internal async Task<ConnectedPlatformAccount> IdentifyCoreAsync(ConnectedPlatformAccount account, CancellationToken ct)
    {
        if (account.Platform == "Epic")
        {
            var me = await GetAsync("https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/verify", account, ct);
            var id = Text(me, "account_id");
            if (id.Length == 0) throw new InvalidDataException("Epic did not identify the signed-in account.");
            return account with { AccountId = id, DisplayName = Text(me, "displayName", "account_id"), ExpiresAt = DateTimeOffset.TryParse(Text(me, "expires_at"), out var expiry) ? expiry : account.ExpiresAt };
        }
        if (account.Platform == "GOG")
        {
            if (account.AccountId.Length == 0) throw new InvalidDataException("GOG did not identify the signed-in account.");
            var me = await GetAsync("https://users.gog.com/users/" + Escape(account.AccountId), account, ct);
            var id = Text(me, "id");
            if (id != account.AccountId) throw new InvalidDataException("The GOG account did not match the sign-in response.");
            return account with { DisplayName = Text(me, "username", "id") };
        }
        if (account.Platform == "EA")
        {
            using var modern = Request(HttpMethod.Post, "https://service-aggregation-layer.juno.ea.com/graphql", account);
            modern.Content = new StringContent("{\"query\":\"query { me { player { pd psd displayName } } }\"}", Encoding.UTF8, "application/json");
            var identity = await SendAsync(modern, ct);
            var player = identity["data"]?["me"]?["player"];
            var modernId = Text(player, "pd");
            if (modernId.Length > 0) return account with { AccountId = modernId, PersonaId = Text(player, "psd"), DisplayName = Text(player, "displayName", "pd") };
            throw new InvalidDataException("EA did not identify the signed-in account.");
        }
        var ubi = await GetAsync("https://public-ubiservices.ubi.com/v3/users/" + Escape(account.AccountId), account, ct);
        if (Text(ubi, "userId") != account.AccountId) throw new InvalidDataException("Ubisoft could not verify the signed-in account.");
        return account with { DisplayName = Text(ubi, "nameOnPlatform", "username", "userId") };
    }
    internal async Task<ConnectedPlatformAccount> VerifyLauncherCandidateAsync(ConnectedPlatformAccount candidate, CancellationToken ct, string? targetAppId = null)
    {
        if (candidate.Platform != "Ubisoft") return await IdentifyCoreAsync(candidate, ct);
        if (targetAppId == null && candidate.SessionSource == "Ubisoft Connect")
        {
            try
            {
                var native = candidate with { AuthAppId = UbisoftOwnershipClient.AppId };
                var profile = await GetAsync("https://public-ubiservices.ubi.com/v3/users/me", native, ct);
                var id = Text(profile, "userId", "id");
                if (!Guid.TryParse(id, out _)) throw new InvalidDataException("Ubisoft did not identify the native launcher account.");
                return native with { AccountId = id, DisplayName = Text(profile, "nameOnPlatform", "username", "userId") };
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { /* The client also holds web-scoped tickets; exchange those below. */ }
        }
        // Exchange the launcher ticket for this reader's application scope; PUT only renews its original scope.
        var target = targetAppId ?? UbisoftAppId;
        using var request = Request(HttpMethod.Post, "https://public-ubiservices.ubi.com/v3/profiles/sessions", candidate with { AuthAppId = target });
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Ubi-RequestedPlatformType", "uplay");
        var result = await SendAsync(request, ct);
        var userId = Text(result, "userId");
        if (!Guid.TryParse(userId, out _) || (candidate.AccountId.Length > 0 && userId != candidate.AccountId) || Text(result, "ticket").Length == 0) throw new InvalidDataException("Ubisoft launcher session did not match the account.");
        return candidate with { AccountId = userId, AccessToken = Text(result, "ticket"), AuthAppId = target, SessionId = Text(result, "sessionId"), DisplayName = Text(result, "nameOnPlatform", "username", "userId"), ExpiresAt = DateTimeOffset.TryParse(Text(result, "expiration"), out var expiry) ? expiry : candidate.ExpiresAt };
    }
    internal async Task<ConnectedPlatformAccount> RefreshAsync(ConnectedPlatformAccount account, CancellationToken ct)
    {
        if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return account;
        if (account.Platform is "Epic" or "GOG" && account.RefreshToken.Length > 0)
        {
            var renewed = await IdentifyCoreAsync(await TokenAsync(account.Platform, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken }, ct, account), ct);
            if (renewed.AccountId != account.AccountId) throw new InvalidDataException("Platform account changed during session renewal. Sign in again.");
            return renewed;
        }
        if (account.Platform == "Ubisoft" && account.RefreshToken.Length > 0)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://public-ubiservices.ubi.com/v3/profiles/sessions") { Content = new StringContent("{\"rememberMe\":true}", Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Authorization", "rm_v1 t=" + account.RefreshToken);
            request.Headers.TryAddWithoutValidation("Ubi-AppId", UbisoftAppId);
            var data = await SendAsync(request, ct);
            if (Text(data, "userId") != account.AccountId || Text(data, "ticket").Length == 0) throw new InvalidDataException("The renewed Ubisoft session did not match the account.");
            return account with { AccessToken = Text(data, "ticket"), RefreshToken = Text(data, "rememberMeTicket"), SessionId = Text(data, "sessionId"), ExpiresAt = DateTimeOffset.TryParse(Text(data, "expiration"), out var expiry) ? expiry : DateTimeOffset.UtcNow.AddHours(1) };
        }
        if (account.Platform == "EA" && account.Cookies.Length > 0)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://accounts.ea.com/connect/auth?client_id=ORIGIN_JS_SDK&response_type=token&redirect_uri=nucleus%3Arest&prompt=none");
            var cookies = JArray.Parse(account.Cookies).OfType<JObject>().Where(c => Text(c, "domain").TrimStart('.') is "accounts.ea.com" or "ea.com").Select(c => Text(c, "name") + "=" + Text(c, "value"));
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
            var data = await SendAsync(request, ct);
            if (Text(data, "access_token").Length == 0) throw new InvalidOperationException("EA sign-in expired. Sign in again.");
            var renewed = await IdentifyCoreAsync(account with { AccessToken = Text(data, "access_token"), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds((int?)data["expires_in"] ?? 1800) }, ct);
            if (renewed.AccountId != account.AccountId) throw new InvalidDataException("EA account changed. Sign in again to choose the account.");
            return renewed;
        }
        throw new InvalidOperationException("Your platform session expired. Sign in again.");
    }
    internal async Task<PlatformCatalogResult> ReadAsync(ConnectedPlatformAccount account, CancellationToken ct, Action<string>? progress = null)
    {
        var games = account.Platform switch {
            "Epic" => await EpicAsync(account, ct, progress),
            "GOG" => await GogAsync(account, ct, progress),
            "EA" => await ModernEaAsync(account, ct, progress),
            "Ubisoft" => await UbisoftAsync(account, ct, progress),
            _ => throw new InvalidDataException("Unknown platform.")
        };
        var missing = games.Count(g => g.ImageUrl.Length == 0);
        return new(games.GroupBy(g => g.Id, StringComparer.Ordinal).Select(g => g.First()).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray(), $"{account.DisplayName} · signed-in account library · refreshed {DateTime.Now:t}" + (missing > 0 ? $" · {missing} covers unavailable" : ""), [account.AccountId]);
    }
    private async Task<PlatformGameRow[]> EpicAsync(ConnectedPlatformAccount account, CancellationToken ct, Action<string>? progress)
    {
        var entries = await CursorPagesAsync("https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true", account, "records", "responseMetadata.nextCursor", "cursor", ct);
        return await EnrichItemsAsync(entries.OfType<JObject>(), async (entry, index) =>
        {
            ct.ThrowIfCancellationRequested();
            string id = Text(entry, "catalogItemId"), ns = Text(entry, "namespace"), app = Text(entry, "appName");
            if (id.Length == 0 || ns.Length == 0) return null;
            progress?.Invoke($"Reading Epic metadata · {index + 1} / {entries.Count}");
            JToken metadata = entry["metadata"] ?? new JObject();
            if (metadata.Type == JTokenType.String) { try { metadata = JToken.Parse(metadata.ToString()); } catch { metadata = new JObject(); } }
            if (Text(metadata, "title", "displayName").Length == 0 || FindImage(metadata).Length == 0)
            {
                try { var catalog = await GetAsync($"https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/{Escape(ns)}/bulk/items?id={Escape(id)}&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en", account, ct); metadata = catalog[id] ?? metadata; }
                catch (Exception error) when (error is HttpRequestException or TimeoutException or InvalidDataException) { /* Keep the owned title even when optional metadata is unavailable. */ }
            }
            if (Text(metadata, "entitlementType").Equals("dlc", StringComparison.OrdinalIgnoreCase) || Text(metadata, "itemType") is "DLC" or "ADD_ON") return null;
            var launch = "com.epicgames.launcher://apps/" + Escape($"{ns}:{id}:{app}") + "?action=launch&silent=true";
            return new("Epic", id, Text(metadata, "title", "displayName") is { Length: > 0 } title ? title : Text(entry, "displayName", "appName", "catalogItemId"), ImageUrl: FindImage(metadata), LaunchUri: app.Length > 0 ? launch : "", Source: "Epic account library", AccountId: account.AccountId, ServiceId: ns);
        }, ct);
    }
    private async Task<PlatformGameRow[]> GogAsync(ConnectedPlatformAccount account, CancellationToken ct, Action<string>? progress)
    {
        var entries = await CursorPagesAsync($"https://galaxy-library.gog.com/users/{Escape(account.AccountId)}/releases", account, "items", "next_page_token", "page_token", ct);
        return await EnrichItemsAsync(entries.OfType<JObject>().Where(e => Text(e, "platform_id") == "gog"), async (entry, index) =>
        {
            ct.ThrowIfCancellationRequested(); var id = Text(entry, "external_id"); if (id.Length == 0) return null;
            progress?.Invoke($"Reading GOG metadata · {index + 1} / {entries.Count}");
            JToken data = new JObject();
            try
            {
                using var request = Request(HttpMethod.Get, $"https://gamesdb.gog.com/platforms/gog/external_releases/{Escape(id)}", account);
                if (Text(entry, "certificate") is { Length: > 0 } cert) request.Headers.TryAddWithoutValidation("X-GOG-Library-Cert", cert);
                data = await SendAsync(request, ct);
            }
            catch (Exception error) when (error is HttpRequestException or TimeoutException or InvalidDataException) { }
            if (Text(data, "type") is "dlc" or "pack") return null;
            var name = data["title"]?["*"]?.ToString() ?? data["game"]?["title"]?["*"]?.ToString() ?? "";
            var image = FindImage(data["game"]);
            if (name.Length == 0 || image.Length == 0)
            {
                try { var product = await GetAsync("https://api.gog.com/products/" + Escape(id), account, ct); name = Text(product, "title") is { Length: > 0 } title ? title : name; image = image.Length > 0 ? image : FindImage(product); }
                catch (Exception error) when (error is HttpRequestException or TimeoutException or InvalidDataException) { }
            }
            return new("GOG", id, name.Length > 0 ? name : "GOG " + id, ImageUrl: image, LaunchUri: "goggalaxy://openGameView/" + Escape(id), Source: "GOG account library", AccountId: account.AccountId);
        }, ct);
    }
    private async Task<PlatformGameRow[]> ModernEaAsync(ConnectedPlatformAccount account, CancellationToken ct, Action<string>? progress)
    {
        async Task<JToken> Query(string query, JObject variables)
        {
            using var request = Request(HttpMethod.Post, "https://service-aggregation-layer.juno.ea.com/graphql", account);
            request.Content = new StringContent(new JObject { ["query"] = query, ["variables"] = variables }.ToString(), Encoding.UTF8, "application/json");
            var response = await SendAsync(request, ct);
            if (response["errors"] is JArray { Count: > 0 }) throw new InvalidDataException("EA did not return a complete account library.");
            return response["data"] ?? throw new InvalidDataException("EA library response had no data.");
        }
        var offers = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var data = await Query("query getEntitlements($limit:Int,$next:String){me{ownedGameProducts(locale:\"DEFAULT\",entitlementEnabled:true,storefronts:[EA],type:[DIGITAL_FULL_GAME,PACKAGED_FULL_GAME],platforms:[PC],paging:{limit:$limit,next:$next}){next items{originOfferId product{baseItem{gameType}}}}}}", new() { ["limit"] = 100, ["next"] = cursor });
            var page = data["me"]?["ownedGameProducts"] ?? throw new InvalidDataException("EA owned library was unavailable.");
            foreach (var entry in page["items"] as JArray ?? throw new InvalidDataException("EA owned library items were unavailable."))
                if (Text((entry["product"] as JObject)?["baseItem"], "gameType") == "BASE_GAME" && Text(entry, "originOfferId") is { Length: > 0 } id) offers.Add(id);
            cursor = Text(page, "next");
            if (cursor.Length > 0 && (!cursors.Add(cursor) || cursors.Count > 1000)) throw new InvalidDataException("EA library pagination did not complete.");
        } while (cursor.Length > 0);
        var games = new List<PlatformGameRow>();
        foreach (var batch in offers.Chunk(100))
        {
            progress?.Invoke($"Reading EA metadata · {games.Count} / {offers.Count}");
            var data = await Query("query getOffers($offerIds:[String!]!){legacyOffers(offerIds:$offerIds,locale:\"DEFAULT\"){offerId:id contentId} gameProducts(offerIds:$offerIds,locale:\"DEFAULT\"){items{id originOfferId baseItem{title packArt{largestImage{path}} keyArt{largestImage{path}}}}}}", new() { ["offerIds"] = new JArray(batch) });
            var products = ((data["gameProducts"] as JObject)?["items"] as JArray ?? []).OfType<JObject>().ToArray();
            var legacy = (data["legacyOffers"] as JArray ?? []).OfType<JObject>().ToArray();
            foreach (var offer in batch)
            {
                var contentId = Text(legacy.FirstOrDefault(l => Text(l, "offerId") == offer), "contentId");
                var product = products.FirstOrDefault(p => Text(p, "originOfferId") == offer) ?? products.FirstOrDefault(p => Text(p, "id") == contentId || Text(p, "id") == offer);
                var name = Text(product?["baseItem"], "title");
                var art = NormalizeImage(Text(product?["baseItem"]?["packArt"]?["largestImage"], "path"));
                if (art.Length == 0) art = NormalizeImage(Text(product?["baseItem"]?["keyArt"]?["largestImage"], "path"));
                games.Add(new("EA", offer, name.Length > 0 ? name : offer, ImageUrl: art, LaunchUri: "origin2://game/launch?offerIds=" + Escape(offer), Source: "EA account library", AccountId: account.AccountId, ServiceId: contentId));
            }
        }
        return games.ToArray();
    }
    private async Task<PlatformGameRow[]> UbisoftAsync(ConnectedPlatformAccount account, CancellationToken ct, Action<string>? progress)
    {
        if (account.SessionSource != "Ubisoft Connect")
        {
            try { return await UbisoftGraphAsync(account, ct, progress); }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException) { }
        }
        progress?.Invoke("Reading the Ubisoft account ownership service…");
        var scoped = account.AuthAppId == UbisoftOwnershipClient.AppId ? account : await VerifyLauncherCandidateAsync(account, ct, UbisoftOwnershipClient.AppId);
        var owned = await UbisoftOwnershipClient.ReadAsync(scoped, ct);
        foreach (var game in owned.Where(g => g.ImageUrl.Length == 0).ToArray())
        {
            try
            {
                var data = await GetAsync("https://gamesdb.gog.com/platforms/uplay/external_releases/" + Escape(game.Id), null, ct);
                var art = FindImage((data as JObject)?["game"]);
                if (art.Length > 0) owned[Array.IndexOf(owned, game)] = game with { ImageUrl = art };
            }
            catch (Exception error) when (error is HttpRequestException or TimeoutException or InvalidDataException) { }
        }
        return owned;
    }
    private async Task<PlatformGameRow[]> UbisoftGraphAsync(ConnectedPlatformAccount account, CancellationToken ct, Action<string>? progress)
    {
        var rows = new List<JToken>(); string? cursor = null; var seen = new HashSet<string>();
        for (var page = 0; page < 1000; page++)
        {
            ct.ThrowIfCancellationRequested();
            // Query fields in stages: a missing optional artwork field must not reject ownership.
            var query = page == 0 ? "query Owned{viewer{games(filterBy:{isOwned:true}){totalCount nodes{id spaceId name lowBoxArtUrl lowThumbnailUrl avatarUrl}}}}" : "query Owned($after:String){viewer{games(filterBy:{isOwned:true},first:100,after:$after){totalCount nodes{id spaceId name} pageInfo{hasNextPage endCursor}}}}";
            JToken response;
            try { response = await GraphAsync("https://public-ubiservices.ubi.com/v1/profiles/me/uplay/graphql", query, new JObject { ["after"] = cursor }, account, ct); }
            catch (InvalidDataException) when (page == 0)
            {
                response = await GraphAsync("https://public-ubiservices.ubi.com/v1/profiles/me/uplay/graphql", "query Owned{viewer{games(filterBy:{isOwned:true}){totalCount nodes{id spaceId name}}}}", new(), account, ct);
            }
            var connection = response.SelectToken("data.viewer.games") ?? throw new InvalidDataException("Ubisoft did not return the signed-in library.");
            rows.AddRange(connection["nodes"] as JArray ?? new JArray());
            progress?.Invoke($"Reading Ubisoft library · {rows.Count} titles");
            if ((bool?)connection.SelectToken("pageInfo.hasNextPage") != true)
            {
                if ((int?)connection["totalCount"] is { } count && rows.Count < count)
                {
                    if (page == 0) { rows.Clear(); continue; }
                    throw new InvalidDataException("Ubisoft returned an incomplete library. Refresh to retry.");
                }
                break;
            }
            cursor = connection.SelectToken("pageInfo.endCursor")?.ToString();
            if (string.IsNullOrEmpty(cursor) || !seen.Add(cursor)) throw new InvalidDataException("Ubisoft library pagination did not advance.");
            if (page == 999) throw new InvalidDataException("Ubisoft library exceeded the supported page limit.");
        }
        return await EnrichItemsAsync(rows.OfType<JObject>(), async (row, index) =>
        {
            ct.ThrowIfCancellationRequested(); var id = Text(row, "id"); if (id.Length == 0) return null;
            var art = FindImage(row);
            if (art.Length == 0) { try { var meta = await GetAsync("https://gamesdb.gog.com/platforms/uplay/external_releases/" + Escape(id), null, ct); art = FindImage(meta["game"]); } catch (Exception error) when (error is HttpRequestException or TimeoutException or InvalidDataException) { } }
            return new("Ubisoft", id, Text(row, "name", "id"), ImageUrl: art, LaunchUri: "uplay://launch/" + Escape(id) + "/0", Source: "Ubisoft account library", AccountId: account.AccountId, ServiceId: Text(row, "spaceId"));
        }, ct);
    }
    private static async Task<PlatformGameRow[]> EnrichItemsAsync(IEnumerable<JObject> source, Func<JObject, int, Task<PlatformGameRow?>> read, CancellationToken ct)
    {
        using var concurrency = new SemaphoreSlim(6);
        var tasks = source.Select(async (row, index) => {
            await concurrency.WaitAsync(ct);
            try { return await read(row, index); }
            finally { concurrency.Release(); }
        }).ToArray();
        return (await Task.WhenAll(tasks)).OfType<PlatformGameRow>().ToArray();
    }
    internal async Task<List<JToken>> CursorPagesAsync(string url, ConnectedPlatformAccount account, string items, string nextField, string parameter, CancellationToken ct)
    {
        var rows = new List<JToken>(); var seen = new HashSet<string>(); var original = url;
        for (var page = 0; page < 1000; page++)
        {
            var data = await GetAsync(url, account, ct);
            if (data[items] is not JArray array) throw new InvalidDataException("The account service did not return a library page.");
            rows.AddRange(array); if (rows.Count > 100000) throw new InvalidDataException("Library exceeded the supported item limit.");
            var next = data.SelectToken(nextField)?.ToString();
            if (string.IsNullOrEmpty(next)) return rows;
            if (!seen.Add(next)) throw new InvalidDataException("Account library pagination repeated a page. No partial catalog was saved.");
            url = original + (original.Contains('?') ? "&" : "?") + parameter + "=" + Escape(next);
        }
        throw new InvalidDataException("Account library exceeded the supported page limit.");
    }
    internal static string FindImage(JToken? data)
    {
        if (data is not JObject) return "";
        foreach (var path in new[] { "vertical_cover.url_format", "cover.url_format", "verticalCover.url", "lowBoxArtUrl", "lowThumbnailUrl", "avatarUrl", "boxArtUrl", "i18n.packArtLarge", "i18n.packArtMedium", "images.logo", "images.icon", "square_icon.url_format", "logo.url_format", "background.url_format", "imageUrl", "image" })
        {
            var value = data.SelectToken(path);
            if (value?.Type == JTokenType.String && NormalizeImage(value.ToString()) is { Length: > 0 } url) return url;
        }
        if (data["keyImages"] is JArray images)
        {
            foreach (var type in new[] { "DieselGameBoxTall", "DieselGameBox", "OfferImageTall", "Thumbnail", "DieselGameBoxLogo" })
                if (images.FirstOrDefault(i => Text(i, "type") == type)?["url"]?.ToString() is { } image && NormalizeImage(image) is { Length: > 0 } url) return url;
            return images.Select(i => NormalizeImage(Text(i, "url"))).FirstOrDefault(i => i.Length > 0) ?? "";
        }
        return "";
    }
    internal static string NormalizeImage(string value)
    {
        value = value.Trim();
        if (value.StartsWith("//")) value = "https:" + value;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[7..];
        value = value.Replace("{formatter}", "").Replace("{ext}", "jpg");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 ? value : "";
    }
    internal static PlatformCatalogResult MergeInstallations(PlatformCatalogResult owned, IEnumerable<PlatformGameRow> installations, string account)
    {
        var local = installations.GroupBy(g => (g.Platform, g.Id)).ToDictionary(g => g.Key, g => g.First());
        return owned with { Games = owned.Games.Select(g => local.TryGetValue((g.Platform, g.Id), out var cached) ? g with {
            InstallPath = cached.InstallPath,
            ImageUrl = g.ImageUrl.Length > 0 ? g.ImageUrl : NormalizeImage(cached.ImageUrl),
            Rows = g.AchievementsLoaded ? g.Rows : cached.AccountId == account ? cached.Rows : g.Rows,
            CatalogComplete = g.AchievementsLoaded ? g.CatalogComplete : cached.AccountId == account && cached.CatalogComplete
        } : g).ToArray() };
    }
    private async Task<JToken> GraphAsync(string url, string query, JObject variables, ConnectedPlatformAccount account, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, url, account);
        request.Content = new StringContent(new JObject { ["query"] = query, ["variables"] = variables }.ToString(), Encoding.UTF8, "application/json");
        var data = await SendAsync(request, ct);
        if (data["errors"] is JArray { Count: > 0 }) throw new InvalidDataException("The platform rejected the library query. No partial library was substituted.");
        return data;
    }
    private Task<JToken> GetAsync(string url, ConnectedPlatformAccount? account, CancellationToken ct, string? accept = null)
    {
        var request = Request(HttpMethod.Get, url, account);
        if (accept != null) request.Headers.TryAddWithoutValidation("Accept", accept);
        return SendOwnedAsync(request, ct);
    }
    private async Task<JToken> SendOwnedAsync(HttpRequestMessage request, CancellationToken ct) { using (request) return await SendAsync(request, ct); }
    private static HttpRequestMessage Request(HttpMethod method, string url, ConnectedPlatformAccount? account)
    {
        var request = new HttpRequestMessage(method, url);
        if (request.RequestUri!.Host == "launcher.store.epicgames.com")
        {
            request.Headers.UserAgent.ParseAdd("EpicGamesLauncher/14.0.8-22004686+++Portal+Release-Live");
            request.Headers.TryAddWithoutValidation("Origin", "https://store.epicgames.com");
        }
        if (account != null)
        {
            if (account.SessionKind == "UbisoftOrbit") throw new InvalidOperationException("A native Ubisoft session cannot be forwarded as an HTTP bearer token.");
            var host = request.RequestUri!.Host;
            var permitted = account.Platform switch {
                "Epic" => host.EndsWith(".epicgames.com", StringComparison.Ordinal) || host.EndsWith(".on.epicgames.com", StringComparison.Ordinal),
                "GOG" => host.EndsWith(".gog.com", StringComparison.Ordinal),
                "EA" => host is "gateway.ea.com" or "api1.origin.com" or "achievements.gameservices.ea.com" or "service-aggregation-layer.juno.ea.com",
                "Ubisoft" => host is "public-ubiservices.ubi.com" or "api-ubiservices.ubi.com",
                _ => false
            };
            if (!permitted || request.RequestUri.Scheme != "https") throw new InvalidOperationException("Credentials cannot be sent to this host.");
            if (account.Platform == "Ubisoft") { request.Headers.TryAddWithoutValidation("Authorization", "Ubi_v1 t=" + account.AccessToken); request.Headers.TryAddWithoutValidation("Ubi-AppId", account.AuthAppId.Length > 0 ? account.AuthAppId : UbisoftAppId); if (account.SessionId.Length > 0) request.Headers.TryAddWithoutValidation("Ubi-SessionId", account.SessionId); }
            else { request.Headers.Authorization = new("Bearer", account.AccessToken); if (account.Platform == "EA") { request.Headers.TryAddWithoutValidation("AuthToken", account.AccessToken); request.Headers.TryAddWithoutValidation("X-AuthToken", account.AccessToken); } }
        }
        return request;
    }
    private async Task<JToken> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage received;
        try { received = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The platform service timed out. Retry the refresh."); }
        catch (HttpRequestException) { throw new HttpRequestException("Could not contact the platform service. Check your connection and retry."); }
        using var response = received; Check(response);
        if (response.Content.Headers.ContentLength > 16_777_216) throw new InvalidDataException("Platform response exceeded its supported size.");
        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Length > 16_777_216) throw new InvalidDataException("Platform response exceeded its supported size.");
        try { return JToken.Parse(text); } catch { throw new InvalidDataException("The platform returned an unexpected response. Sign in again or retry later."); }
    }
    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new HttpRequestException("Platform authorization was rejected. Sign in again.", null, response.StatusCode);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Platform request failed (HTTP {(int)response.StatusCode}). Retry later.", null, response.StatusCode);
    }
    internal static string Text(JToken? data, params string[] keys) => data is JObject obj ? keys.Select(k => obj[k]).Where(v => v?.Type is JTokenType.String or JTokenType.Integer).Select(v => v!.ToString()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "" : "";
    private static string Escape(string value) => Uri.EscapeDataString(value);
}
