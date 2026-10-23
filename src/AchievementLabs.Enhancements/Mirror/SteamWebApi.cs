using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AchievementLabs.Mirror;

public sealed class SteamWebApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Reads a player's achievements from Steam's official Web API. This works no matter which device the
/// game is played on (PC, Steam Deck, Steam Machine), which is why the Mirror uses it as its source of truth.
/// The key is never written to logs or exception messages.
/// </summary>
public sealed class SteamWebApi(HttpClient http)
{
    public const string BaseUrl = "https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/";

    public static string BuildUrl(string key, string steamId, long appId) =>
        $"{BaseUrl}?key={Uri.EscapeDataString(key)}&steamid={Uri.EscapeDataString(steamId)}&appid={appId.ToString(CultureInfo.InvariantCulture)}&l=english";

    public async Task<IReadOnlyList<MirrorAchievement>> GetPlayerAchievementsAsync(string key, string steamId, long appId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new SteamWebApiException("Add your Steam Web API key in Achievement Mirror first.");
        if (!IsSteamId64(steamId)) throw new SteamWebApiException("Steam account ID is missing. Sign in to Steam on this PC or enter your SteamID64.");
        using var response = await http.GetAsync(BuildUrl(key.Trim(), steamId.Trim(), appId), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SteamWebApiException("Steam rejected the Web API key. Check it on steamcommunity.com/dev/apikey.", response.StatusCode);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SteamWebApiException("Steam is rate limiting requests. The Mirror will try again shortly.", response.StatusCode);
        // Steam answers 400/403 with a JSON error for private profiles and games without stats.
        if (!response.IsSuccessStatusCode && !body.TrimStart().StartsWith('{'))
            throw new SteamWebApiException($"Steam Web API returned HTTP {(int)response.StatusCode}.", response.StatusCode);
        return Parse(body);
    }

    public const string SummariesUrl = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/";

    /// <summary>App ID the player is currently in on any device, or 0. Needs a profile Steam lets the key read.</summary>
    public async Task<long> GetPlayingAppIdAsync(string key, string steamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || !IsSteamId64(steamId)) return 0;
        using var response = await http.GetAsync($"{SummariesUrl}?key={Uri.EscapeDataString(key.Trim())}&steamids={Uri.EscapeDataString(steamId.Trim())}", cancellationToken);
        if (!response.IsSuccessStatusCode) throw new SteamWebApiException($"Steam Web API returned HTTP {(int)response.StatusCode} for your current game.", response.StatusCode);
        return ParsePlayingAppId(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public static long ParsePlayingAppId(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out var root) || !root.TryGetProperty("players", out var players)
            || players.ValueKind != JsonValueKind.Array || players.GetArrayLength() == 0) return 0;
        var player = players[0];
        return player.TryGetProperty("gameid", out var game) && long.TryParse(game.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    public static IReadOnlyList<MirrorAchievement> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("playerstats", out var stats))
            throw new SteamWebApiException("Steam Web API returned an unexpected response.");
        if (stats.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            var error = stats.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            throw new SteamWebApiException(error.Contains("not public", StringComparison.OrdinalIgnoreCase)
                ? "Steam says your game details are private. Set Game details to Public in your Steam privacy settings, then try again."
                : string.IsNullOrWhiteSpace(error) ? "Steam Web API did not return achievements for this game." : "Steam Web API: " + error);
        }
        if (!stats.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<MirrorAchievement>();
        foreach (var item in list.EnumerateArray())
        {
            var id = item.TryGetProperty("apiname", out var api) ? api.GetString() ?? "" : "";
            if (id.Length == 0) continue;
            var unlocked = item.TryGetProperty("achieved", out var achieved) && achieved.ValueKind == JsonValueKind.Number && achieved.GetInt32() == 1;
            DateTimeOffset? at = item.TryGetProperty("unlocktime", out var time) && time.ValueKind == JsonValueKind.Number && time.GetInt64() > 0
                ? DateTimeOffset.FromUnixTimeSeconds(time.GetInt64()) : null;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            result.Add(new(id, name, description, unlocked, unlocked ? at : null));
        }
        return result;
    }

    public static bool IsSteamId64(string? value) =>
        ulong.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 76561197960265728UL;

    /// <summary>The 32-bit account number Steam uses in local file names (SteamID64 − 76561197960265728).</summary>
    public static uint AccountId(string steamId64) => (uint)(ulong.Parse(steamId64.Trim(), CultureInfo.InvariantCulture) - 76561197960265728UL);
}
