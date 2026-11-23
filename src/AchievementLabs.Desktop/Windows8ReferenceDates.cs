using System.Globalization;
using AchievementLabs.Core;
using AchievementLabs.Models;
using AchievementLabs.Services.Xbox360;

namespace AchievementLabs.Desktop;

internal static class Windows8ReferenceDates
{
    internal static AchievementsResponse Match(AchievementsResponse catalog,
        IReadOnlyList<Xbox360ReferenceUnlock> imported, IReadOnlyList<ExophaseAwardIdentity>? awardIdentities = null)
    {
        if (catalog.achievements.Count == 0)
            throw new InvalidDataException("Xbox did not return this Windows 8 title's achievement catalog.");
        var definitions = catalog.achievements.GroupBy(a => TitleSearchText.Key(a.name))
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var entries = imported.GroupBy(a => TitleSearchText.Key(a.Name))
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var result = new List<OneCoreAchievementResponse>();
        foreach (var (key, matches) in entries)
        {
            if (!definitions.TryGetValue(key, out var targets)) continue;
            if (key.Length == 0 || matches.Length != 1 || targets.Length != 1)
                throw new InvalidDataException("An imported achievement name is ambiguous. Check the saved reference file.");
            if (matches[0].Time is { } time)
                result.Add(Achieved(targets[0], targets[0].name, time));
        }
        if (result.Count == 0 && awardIdentities != null)
        {
            // Award IDs are only accepted when the entire title has matching unique IDs and points.
            var byId = catalog.achievements.GroupBy(a => a.id)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            if (awardIdentities.Count == catalog.achievements.Count && awardIdentities.Count == imported.Count &&
                awardIdentities.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() == awardIdentities.Count &&
                awardIdentities.Select(a => TitleSearchText.Key(a.Name)).Distinct(StringComparer.Ordinal).Count() == awardIdentities.Count &&
                byId.Values.All(group => group.Length == 1) &&
                awardIdentities.All(a => uint.TryParse(a.Id, out _) && byId.TryGetValue(a.Id, out var targets) &&
                    int.TryParse(targets[0].gamerscore?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var points) &&
                    points > 0 && points == a.Points &&
                    entries.TryGetValue(TitleSearchText.Key(a.Name), out var rows) && rows.Length == 1))
            {
                foreach (var award in awardIdentities)
                    if (entries[TitleSearchText.Key(award.Name)][0].Time is { } time)
                        result.Add(Achieved(byId[award.Id][0], byId[award.Id][0].name, time));
            }
        }
        if (result.Count == 0)
            throw new InvalidDataException("No dated achievements in the saved reference matched this Windows 8 title's catalog. Check the game edition and names.");
        return new AchievementsResponse { achievements = result };
    }

    private static OneCoreAchievementResponse Achieved(OneCoreAchievementResponse definition, string name, DateTimeOffset time) =>
        new()
        {
            id = definition.id, name = name, description = definition.description,
            gamerscore = definition.gamerscore, progressState = "Achieved",
            progression = new AchievementProgression { timeUnlocked = time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) }
        };
}
