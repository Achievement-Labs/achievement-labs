using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Desktop.Platforms;

internal sealed partial class AccountLibraryClient
{
    internal async Task<PlatformAchievementsResult> ReadAchievementsAsync(ConnectedPlatformAccount account, PlatformGameRow game, CancellationToken ct)
    {
        if (game.Platform != account.Platform || game.AccountId != account.AccountId) throw new InvalidDataException("Achievement account does not match the owned game.");
        return account.Platform switch
        {
            "Epic" => await EpicAchievementsAsync(account, game, ct),
            "EA" => await EaAchievementsAsync(account, game, ct),
            "GOG" => await GogAchievementsAsync(account, game, ct),
            "Ubisoft" => await UbisoftAchievementsAsync(account, game, ct),
            _ => throw new InvalidDataException("Unknown achievement platform.")
        };
    }
    private async Task<PlatformAchievementsResult> EpicAchievementsAsync(ConnectedPlatformAccount account, PlatformGameRow game, CancellationToken ct)
    {
        if (game.ServiceId.Length == 0) throw new InvalidDataException("Epic sandbox ID is missing. Refresh the library and retry.");
        var variables = new JObject { ["sandboxId"] = game.ServiceId, ["locale"] = "en", ["epicAccountId"] = account.AccountId };
        const string definitions = "query Achievement($sandboxId:String!,$locale:String!){Achievement{productAchievementsRecordBySandbox(sandboxId:$sandboxId,locale:$locale){productId sandboxId totalAchievements achievements{achievement{name hidden unlockedDisplayName lockedDisplayName unlockedDescription lockedDescription unlockedIconLink lockedIconLink rarity{percent}}}}}}";
        var data = await GraphAsync("https://launcher.store.epicgames.com/graphql", definitions, new JObject { ["sandboxId"] = game.ServiceId, ["locale"] = "en" }, account, ct);
        var record = data.SelectToken("data.Achievement.productAchievementsRecordBySandbox") as JObject;
        if (record == null) return new([], true, "Epic exposes no achievement catalog for this title.");
        if (Text(record, "sandboxId") is { Length: > 0 } sandbox && sandbox != game.ServiceId) throw new InvalidDataException("Epic returned achievements for a different game.");
        var definitionsArray = record["achievements"] as JArray ?? throw new InvalidDataException("Epic achievement definitions did not load.");
        var states = new Dictionary<string, JObject>(StringComparer.Ordinal);
        var playerKnown = false;
        var progressReadError = "";
        try
        {
            JArray records;
            if (Text(record, "productId") is { Length: > 0 } productId)
            {
                const string player = "query playerProfileAchievementsByProductId($epicAccountId:String!,$productId:String!){PlayerProfile{playerProfile(epicAccountId:$epicAccountId){epicAccountId productAchievements(productId:$productId){__typename ... on PlayerProductAchievementsResponseSuccess{data{epicAccountId sandboxId playerAchievements{playerAchievement{sandboxId epicAccountId achievementName unlocked unlockDate}}}}}}}}";
                var response = await GraphAsync("https://launcher.store.epicgames.com/graphql", player, new JObject { ["epicAccountId"] = account.AccountId, ["productId"] = productId }, account, ct);
                var profile = response.SelectToken("data.PlayerProfile.playerProfile") as JObject ?? throw new InvalidDataException("Epic player profile was unavailable.");
                if (Text(profile, "epicAccountId") != account.AccountId) throw new InvalidDataException("Epic player profile did not match the connected account.");
                if (profile.SelectToken("productAchievements.data") is JObject progress)
                {
                    if (Text(progress, "epicAccountId") != account.AccountId) throw new InvalidDataException("Epic player progress did not match the connected account.");
                    records = new JArray(progress);
                }
                else records = await ReadSandboxStates();
            }
            else
            {
                records = await ReadSandboxStates();
            }
            foreach (var item in records.OfType<JObject>().SelectMany(r => (r["playerAchievements"] as JArray ?? []).OfType<JObject>()))
            {
                if (item["playerAchievement"] is not JObject state) continue;
                if (Text(state, "epicAccountId") is { Length: > 0 } id && id != account.AccountId) throw new InvalidDataException("Epic player achievement account mismatch.");
                if (Text(state, "sandboxId") is { Length: > 0 } ns && ns != game.ServiceId) throw new InvalidDataException("Epic player achievement sandbox mismatch.");
                if (Text(state, "achievementName") is { Length: > 0 } key) states[key] = state;
            }
            playerKnown = true;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TimeoutException)
        {
            progressReadError = error is HttpRequestException request && request.StatusCode.HasValue ? "HTTP " + (int)request.StatusCode : error.Message;
        }
        var rows = definitionsArray.OfType<JObject>().Select(d => d["achievement"]).OfType<JObject>().Select(definition =>
        {
            var id = Text(definition, "name");
            var state = states.GetValueOrDefault(id);
            bool? unlocked = playerKnown ? (bool?)state?["unlocked"] ?? false : null;
            return new PlatformAchievementRow(id, Text(definition, unlocked == true ? "unlockedDisplayName" : "lockedDisplayName", "unlockedDisplayName", "name"), Text(definition, unlocked == true ? "unlockedDescription" : "lockedDescription", "unlockedDescription"), unlocked,
                NormalizeImage(Text(definition, unlocked == true ? "unlockedIconLink" : "lockedIconLink", "unlockedIconLink")), Percentage(definition["rarity"]?["percent"]), Text(state, "unlockDate"), "Epic account achievements");
        }).Where(r => r.Id.Length > 0).GroupBy(r => r.Id).Select(g => g.First()).ToArray();
        var complete = playerKnown && ((int?)record["totalAchievements"] is not { } total || total == rows.Length);
        return new(rows, complete, rows.Length == 0 ? "Epic returned an empty achievement catalog." : playerKnown ? $"{rows.Length} Epic achievements loaded." : $"{rows.Length} Epic definitions loaded; player progress could not be read ({progressReadError}). Refresh achievements to retry.");
        async Task<JArray> ReadSandboxStates()
        {
            const string query = "query PlayerAchievement($epicAccountId:String!,$sandboxId:String!){PlayerAchievement{playerAchievementGameRecordsBySandbox(epicAccountId:$epicAccountId,sandboxId:$sandboxId){records{playerAchievements{playerAchievement{sandboxId epicAccountId achievementName unlocked unlockDate}}}}}}";
            var response = await GraphAsync("https://launcher.store.epicgames.com/graphql", query, new JObject { ["epicAccountId"] = account.AccountId, ["sandboxId"] = game.ServiceId }, account, ct);
            return response.SelectToken("data.PlayerAchievement.playerAchievementGameRecordsBySandbox.records") as JArray ?? throw new InvalidDataException("Epic player achievement states were unavailable.");
        }
    }
    private async Task<PlatformAchievementsResult> EaAchievementsAsync(ConnectedPlatformAccount account, PlatformGameRow game, CancellationToken ct)
    {
        if (account.PersonaId.Length == 0) account = await IdentifyCoreAsync(account, ct);
        if (account.PersonaId.Length == 0) throw new InvalidDataException("EA persona is unavailable. Reattach the account and retry.");
        const string query = "query GetAchievements($offerId:String!,$playerPsd:String!,$locale:Locale!){achievements(offerId:$offerId,playerPsd:$playerPsd,showHidden:true,locale:$locale){id achievements{id name description awardCount date}}}";
        var data = await GraphAsync("https://service-aggregation-layer.juno.ea.com/graphql", query, new JObject { ["offerId"] = game.Id, ["playerPsd"] = account.PersonaId, ["locale"] = "US" }, account, ct);
        var sets = data.SelectToken("data.achievements") as JArray ?? throw new InvalidDataException("EA did not return an achievement catalog.");
        var rows = sets.OfType<JObject>().SelectMany(set => (set["achievements"] as JArray ?? []).OfType<JObject>().Select(a =>
        {
            var id = Text(a, "id");
            bool? unlocked = a["awardCount"]?.Type == JTokenType.Integer ? (int)a["awardCount"]! > 0 : null;
            return new PlatformAchievementRow(Text(set, "id") + ":" + id, Text(a, "name", "id"), Text(a, "description"), unlocked, UnlockTime: unlocked == true ? Text(a, "date") : "", Source: "EA account achievements");
        })).GroupBy(a => a.Id).Select(g => g.First()).ToArray();
        return new(rows, rows.All(r => r.Unlocked.HasValue), rows.Length == 0 ? "EA exposes no achievement set for this title." : $"{rows.Length} EA achievements loaded. EA's current API does not expose achievement icons or rarity.");
    }
    private async Task<PlatformAchievementsResult> GogAchievementsAsync(ConnectedPlatformAccount account, PlatformGameRow game, CancellationToken ct)
    {
        try
        {
            var rows = new List<PlatformAchievementRow>(); var seen = new HashSet<string>(); string? cursor = null; int? total = null;
            for (var page = 0; page < 100; page++)
            {
                var url = $"https://gameplay.gog.com/clients/{Escape(game.Id)}/users/{Escape(account.AccountId)}/achievements?limit=1000" + (cursor == null ? "" : "&page_token=" + Escape(cursor));
                var response = await GetAsync(url, account, ct);
                var items = response["items"] as JArray ?? throw new InvalidDataException("GOG did not return an achievement catalog.");
                total ??= (int?)response["total_count"];
                rows.AddRange(items.OfType<JObject>().Select(a =>
                {
                    bool? unlocked = a.ContainsKey("date_unlocked") ? a["date_unlocked"]?.Type is not (JTokenType.Null or JTokenType.Undefined) : null;
                    return new PlatformAchievementRow(Text(a, "achievement_key", "achievement_id", "id"), Text(a, "name", "achievement_key", "achievement_id"), Text(a, "description"), unlocked,
                        NormalizeImage(Text(a, unlocked == true ? "image_url_unlocked" : "image_url_locked", "image_url_unlocked", "imageUrlUnlocked")), Percentage(a["rarity"]), unlocked == true ? Text(a, "date_unlocked") : "", "GOG account achievements");
                }));
                var unique = rows.GroupBy(r => r.Id).Select(g => g.First()).ToArray();
                if (total.HasValue && unique.Length >= total) return new(unique, unique.Length == total && unique.All(r => r.Unlocked.HasValue), unique.Length == 0 ? "GOG exposes no achievements for this title." : $"{unique.Length} GOG achievements loaded.");
                cursor = Text(response, "next_page_token", "page_token");
                if (cursor.Length == 0 || !seen.Add(cursor)) throw new InvalidDataException("GOG achievement pagination did not complete.");
            }
            throw new InvalidDataException("GOG achievement pagination exceeded its supported limit.");
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TimeoutException)
        {
            var cached = await Task.Run(() => GogCatalogReader.Read(GogCatalogReader.DefaultDatabase, account.AccountId, ct), ct);
            var gameCache = cached.Games.FirstOrDefault(g => g.Id == game.Id && g.AccountId == account.AccountId && g.Total > 0);
            if (gameCache != null) return new(gameCache.Achievements, gameCache.CatalogComplete, $"{gameCache.Total} achievements from this account's Galaxy cache; online refresh unavailable.");
            throw;
        }
    }
    private async Task<PlatformAchievementsResult> UbisoftAchievementsAsync(ConnectedPlatformAccount account, PlatformGameRow game, CancellationToken ct)
    {
        var definitions = await Task.Run(() => UbisoftAchievementCatalog.ReadAsync(game, ct), ct);
        if (definitions.Length == 0)
            return game.Achievements.Length > 0 ? new(game.Achievements, game.CatalogComplete, "Cached Ubisoft achievement rows; no current definition bundle available.") : new([], false, "Ubisoft has not supplied an achievement definition bundle for this title. Open its achievements in Ubisoft Connect, then refresh here.");
        Dictionary<string, Dictionary<string, string>>? states = null;
        try
        {
            if (account.AccessToken.Length == 0) throw new InvalidOperationException("No verified Ubisoft session available.");
            var scoped = account.AuthAppId == UbisoftOwnershipClient.AppId ? account : await VerifyLauncherCandidateAsync(account, ct, UbisoftOwnershipClient.AppId);
            states = await UbisoftOwnershipClient.ReadAchievementStatesAsync(scoped, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or HttpRequestException or InvalidOperationException or System.Security.Authentication.AuthenticationException) { }
        var earned = states?.GetValueOrDefault(game.AchievementProductId) ?? states?.GetValueOrDefault(game.Id) ?? states?.GetValueOrDefault(game.ServiceId) ?? new Dictionary<string, string>();
        var rows = definitions.Select(d => d with { Unlocked = states == null ? null : earned.ContainsKey(d.Id), UnlockTime = earned.GetValueOrDefault(d.Id) ?? "", Source = states == null ? "Ubisoft achievement definitions; progress unavailable" : "Ubisoft account achievements" }).ToArray();
        return new(rows, states != null, states == null ? $"{rows.Length} Ubisoft achievements loaded; player progress could not be verified. Keep Connect signed in and refresh achievements." : $"{rows.Length} Ubisoft achievements loaded.");
    }
    private static double? Percentage(JToken? value) => double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 100 ? number : null;
}
