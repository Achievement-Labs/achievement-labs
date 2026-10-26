using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchievementLabs.Models;

namespace AchievementLabs.Services.Xbox360;

public sealed record Xbox360ReferenceUnlock(string Name, DateTimeOffset? Time);
public sealed record Xbox360TimestampPreviewRow(string Name, string AchievementId, string CurrentState,
    string ProposedState, string TimeUtc, string Result);

/// <summary>Reads untrusted reference data and produces a review only. Never writes a profile.</summary>
public static class Xbox360TimestampPreview
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    public static IReadOnlyList<Xbox360ReferenceUnlock> Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024)
            throw new InvalidDataException("Reference files must be 2 MB or smaller.");
        var result = new List<Xbox360ReferenceUnlock>();
        if (text.TrimStart().StartsWith('{'))
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            if (!json.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Expected an Exophase JSON object with a list of unlocks.");
            foreach (var item in list.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) && item.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String)
                    name = Uri.UnescapeDataString(slug.GetString()!).Replace('-', ' ');
                if (!item.TryGetProperty("timestamp", out var timestamp) || !timestamp.TryGetInt64(out var seconds))
                    throw new InvalidDataException("Each reference unlock needs an explicit numeric Unix timestamp; 0 means earned offline.");
                Add(name, seconds);
            }
        }
        else
        {
            foreach (Match item in Regex.Matches(text, @"<li\b(?<attrs>[^>]*)>(?<body>.*?)</li\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout))
            {
                var attrs = item.Groups["attrs"].Value;
                if (Attribute(attrs, "data-award-id") == null) continue;
                var classes = Attribute(attrs, "class") ?? "";
                if (!classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("earned", StringComparer.OrdinalIgnoreCase)) continue;
                var earned = Attribute(attrs, "data-earned");
                if (earned == null) throw new InvalidDataException("Save the fully loaded player's achievements page, including its unlock dates.");
                if (!long.TryParse(earned.Length == 0 ? "0" : earned, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    throw new InvalidDataException("The page contains an invalid unlock timestamp.");
                var title = Regex.Match(item.Groups["body"].Value, "award-title[^>]*>\\s*<a\\b[^>]*>(?<name>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
                if (!title.Success) throw new InvalidDataException("An earned entry is missing its achievement title.");
                Add(WebUtility.HtmlDecode(Regex.Replace(title.Groups["name"].Value, "<[^>]+>", "", RegexOptions.None, RegexTimeout)), seconds);
            }
        }
        if (result.Count == 0) throw new InvalidDataException("No earned achievements found. Use earned JSON or a fully loaded saved HTML page; MHTML is not supported in this prototype.");
        return result;

        void Add(string? name, long seconds)
        {
            if (result.Count >= 5000 || string.IsNullOrWhiteSpace(name) || name.Length > 500)
                throw new InvalidDataException("Invalid reference name or too many unlocks.");
            DateTimeOffset? time = seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (seconds < 0 || time < new DateTimeOffset(2005, 11, 22, 0, 0, 0, TimeSpan.Zero) || time > DateTimeOffset.UtcNow)
                throw new InvalidDataException("A timestamp is before Xbox 360 launch or in the future.");
            result.Add(new(name.Trim(), time));
        }
    }

    private static string? Attribute(string attrs, string name)
    {
        var match = Regex.Match(attrs, "(?:^|\\s)" + Regex.Escape(name) + "\\s*=\\s*([\"'])(?<value>.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }

    private static string Key(string value) => string.Join(" ", Regex.Matches(value.Normalize(NormalizationForm.FormKC).ToUpperInvariant(), @"[\p{L}\p{N}]+", RegexOptions.None, RegexTimeout).Select(m => m.Value));

    public static IReadOnlyList<Xbox360TimestampPreviewRow> MatchXbox(IReadOnlyList<Xbox360ProfileAchievement> local,
        IReadOnlyList<Xbox360AchievementEntry> reference,IReadOnlySet<uint> selected,uint titleId)
    {
        if (selected.Count == 0) throw new InvalidOperationException("Select at least one achievement.");
        if (reference.Count > 5000 || reference.Any(a => a.titleId != titleId)) throw new InvalidDataException("Xbox returned a different title or too many achievements.");
        var grouped = reference.Where(a => a.id > 0).GroupBy(a => (uint)a.id).ToDictionary(g => g.Key,g => g.ToArray());
        return local.Where(a => selected.Contains(a.Id)).Select(a =>
        {
            var matches = grouped.GetValueOrDefault(a.Id) ?? [];
            var reason = (a.Flags & 0x20000) != 0 ? "Skipped: already unlocked" : matches.Length > 1 ? "Skipped: duplicate Xbox ID" : matches.Length == 0
                ? "Skipped: missing Xbox ID" : matches[0].unlocked != true ? "Skipped: reference not unlocked" : matches[0].gamerscore != a.Gamerscore
                ? "Skipped: gamerscore mismatch" : matches[0].unlockedOnline == null ? "Skipped: unknown online/offline state" : "Ready for review";
            DateTimeOffset? date = null;
            if (reason == "Ready for review" && matches[0].unlockedOnline == true)
            {
                var text = matches[0].timeUnlocked ?? "";
                if (!Regex.IsMatch(text,@"(?:Z|[+-]\d{2}:\d{2})$",RegexOptions.CultureInvariant,RegexTimeout) ||
                    !DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed) ||
                    parsed < new DateTimeOffset(2005,11,22,0,0,0,TimeSpan.Zero) || parsed > DateTimeOffset.UtcNow)
                    reason = "Skipped: invalid Xbox timestamp";
                else date = parsed;
            }
            return new Xbox360TimestampPreviewRow(a.Name,a.Id.ToString(CultureInfo.InvariantCulture),a.Status,
                reason != "Ready for review" ? "—" : date.HasValue ? "Unlocked Online" : "Unlocked Offline",
                date?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'",CultureInfo.InvariantCulture) ?? "—",reason);
        }).ToArray();
    }

    public static IReadOnlyList<Xbox360TimestampPreviewRow> Match(IReadOnlyList<Xbox360ProfileAchievement> achievements,
        IReadOnlyList<Xbox360ReferenceUnlock> reference, IReadOnlySet<uint> selected)
    {
        if (selected.Count == 0) throw new InvalidOperationException("Select at least one achievement. An empty selection never means the whole game.");
        var local = achievements.GroupBy(a => Key(a.Name)).ToDictionary(g => g.Key, g => g.ToArray());
        var remote = reference.GroupBy(a => Key(a.Name)).ToDictionary(g => g.Key, g => g.ToArray());
        return achievements.Where(a => selected.Contains(a.Id)).Select(a =>
        {
            var key = Key(a.Name);
            var matches = remote.GetValueOrDefault(key) ?? [];
            var reason = (a.Flags & 0x20000) != 0 ? "Skipped: already unlocked" : local[key].Length != 1 || matches.Length > 1
                ? "Skipped: ambiguous name" : matches.Length == 0 ? "Skipped: no exact normalized name match" : "Ready for review";
            var match = reason == "Ready for review" ? matches[0] : null;
            return new Xbox360TimestampPreviewRow(a.Name, a.Id.ToString(CultureInfo.InvariantCulture), a.Status,
                match == null ? "—" : match.Time.HasValue ? "Unlocked Online" : "Unlocked Offline",
                match?.Time?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "—", reason);
        }).ToArray();
    }
}
