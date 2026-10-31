using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Steam;

public sealed partial class SteamAchievementService
{
    // Enumerate the signed-in user's ownership first. Installation files never determine membership.
    public async Task<IReadOnlyList<SteamGameItem>> GetAccountLibraryAsync(string apiKey, CancellationToken token)
    {
        var user = GetLocalSteamId();
        if (!AchievementLabs.Mirror.SteamWebApi.IsSteamId64(user)) throw new InvalidOperationException("Sign in to Steam on this PC before refreshing your account library.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        List<SteamGameItem> games;
        try
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(apiKey)}&steamid={user}&include_appinfo=1&include_played_free_games=1&include_free_sub=1&skip_unvetted_apps=0";
                using var response = await http.GetAsync(url, token);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Steam rejected the account library request. Check the saved Steam Web API key.");
                var data = JObject.Parse(await response.Content.ReadAsStringAsync(token))["response"];
                if (data?["game_count"] == null) throw new InvalidOperationException("Steam did not expose this account's games. Check game-details privacy and your Web API key.");
                var rows = data["games"] as JArray ?? new JArray();
                if ((int)data["game_count"]! != rows.Count) throw new InvalidOperationException("Steam returned an incomplete account library. Refresh to retry.");
                games = rows.OfType<JObject>().Select(row => new SteamGameItem { AppId = (long?)row["appid"] ?? 0, Name = (string?)row["name"] ?? "", Source = "Steam account library" }).Where(g => g.AppId > 0).ToList();
            }
            else
            {
                var text = await http.GetStringAsync($"https://steamcommunity.com/profiles/{user}/games/?tab=all&xml=1", token);
                var document = XDocument.Parse(text);
                if (document.Root?.Name.LocalName != "gamesList" || document.Root.Element("games") == null) throw new InvalidOperationException("Steam's public game list is unavailable. Add your Steam Web API key to read your account library.");
                games = document.Root.Element("games")!.Elements("game").Select(row => new SteamGameItem { AppId = long.TryParse(row.Element("appID")?.Value, out var id) ? id : 0, Name = row.Element("name")?.Value ?? "", Source = "Steam account library" }).Where(g => g.AppId > 0).ToList();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch { throw new InvalidOperationException("Could not read the signed-in Steam account's library. Check your connection, game-details privacy, or Steam Web API key."); }
        var local = (await Task.Run(GetInstalledGames, token)).GroupBy(g => g.AppId).ToDictionary(g => g.Key, g => g.First());
        foreach (var game in games)
        {
            if (local.TryGetValue(game.AppId, out var installed)) { game.InstallPath = installed.InstallPath; game.SchemaPath = installed.SchemaPath; if (game.Name.Length == 0) game.Name = installed.DisplayName; }
            game.ImageUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{game.AppId.ToString(CultureInfo.InvariantCulture)}/header.jpg";
        }
        return games.GroupBy(g => g.AppId).Select(g => g.First()).OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<SteamAchievementItem>> LoadAccountAchievementsAsync(long appId, string apiKey, CancellationToken token)
    {
        if (File.Exists(GetSchemaPath(GetSteamInstallPath(), appId))) return await LoadAchievementsAsync(appId, token);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Add your Steam Web API key to read achievements for games whose schema is not cached on this PC.");
        var user = GetLocalSteamId();
        if (!AchievementLabs.Mirror.SteamWebApi.IsSteamId64(user)) throw new InvalidOperationException("Sign in to Steam first.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        try
        {
            var schemaText = await http.GetStringAsync($"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={Uri.EscapeDataString(apiKey)}&appid={appId}&l=english", token);
            var schema = JObject.Parse(schemaText)["game"]?["availableGameStats"]?["achievements"] as JArray ?? new JArray();
            var playerText = await http.GetStringAsync($"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={Uri.EscapeDataString(apiKey)}&steamid={user}&appid={appId}&l=english", token);
            var player = JObject.Parse(playerText)["playerstats"];
            if ((bool?)player?["success"] != true) throw new InvalidOperationException("Steam did not return this user's achievement states.");
            var states = (player["achievements"] as JArray ?? new JArray()).OfType<JObject>().GroupBy(a => (string?)a["apiname"] ?? "").ToDictionary(g => g.Key, g => g.First());
            return schema.OfType<JObject>().Where(a => a["name"] != null).Select(a =>
            {
                var id = (string)a["name"]!;
                states.TryGetValue(id, out var state);
                var unlocked = (int?)state?["achieved"] == 1;
                return new SteamAchievementItem { Id = id, Name = (string?)a["displayName"] ?? id, Description = (string?)a["description"] ?? "", IconUrl = (string?)a["icon"] ?? "", LockedIconUrl = (string?)a["icongray"] ?? "", IsUnlocked = unlocked, DesiredUnlocked = unlocked, UnlockTime = unlocked && (long?)state?["unlocktime"] is > 0 and var timestamp ? DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime : null };
            }).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch { throw new InvalidOperationException("Steam account achievements could not be read. Check the saved Web API key and game-details privacy."); }
    }
}
