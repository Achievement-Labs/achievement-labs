using System.Text.Json;

namespace AchievementLabs.Mirror;

/// <summary>Reads the user's own Xbox presence (userpresence.xboxlive.com, contract version 3).</summary>
public static class XboxPresence
{
    public static string Url(string xuid) => $"https://userpresence.xboxlive.com/users/xuid({xuid})?level=title";

    /// <summary>
    /// True when any of the user's devices shows <paramref name="titleId"/> as an active title. Presence
    /// Achievement Labs itself sends (title spoofer, Auto Unlock) counts too, which is intended.
    /// </summary>
    public static bool IsInTitle(string json, string titleId)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return false;
        foreach (var device in devices.EnumerateArray())
        {
            if (!device.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array) continue;
            foreach (var title in titles.EnumerateArray())
            {
                var id = title.TryGetProperty("id", out var i) ? i.ValueKind == JsonValueKind.Number ? i.GetRawText() : i.GetString() : null;
                if (id != titleId) continue;
                var state = title.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (state == null || string.Equals(state, "Active", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
