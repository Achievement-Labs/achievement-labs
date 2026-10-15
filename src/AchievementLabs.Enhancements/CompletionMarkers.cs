namespace AchievementLabs.MultiSelect;

// This calculates local display indicators; it does not award an Xbox Mythic achievement.
public static class CompletionMarkers
{
    public sealed record Row(string Id, string Name, bool Unlocked, bool Known = true);
    public sealed record Result(bool BaseComplete, bool AddOnsComplete, bool FullyComplete = false);
    public static Result Evaluate(SharedDlcCatalogue.Title? title, IEnumerable<Row> progress,
        int total, int unlocked, bool aggregateKnown)
    {
        var rows = progress.ToArray();
        if (title == null)
            return new(aggregateKnown && total > 0 && unlocked == total, false,
                aggregateKnown && total > 0 && unlocked == total);
        var definitions = title.Packs.SelectMany(p => p.Achievements).ToArray();
        var allComplete = aggregateKnown && total == definitions.Length && unlocked == total && total > 0;
        var byId = rows.GroupBy(r => r.Id, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        bool Earned(SharedDlcCatalogue.Definition a) => byId.TryGetValue(a.Id, out var r) &&
            r.Known && r.Unlocked && AchievementView.Normalize(a.Name) == AchievementView.Normalize(r.Name);
        var baseGame = title.Packs.Where(p => p.Kind == "base").SelectMany(p => p.Achievements).ToArray();
        var addOns = title.Packs.Where(p => p.Kind != "base").SelectMany(p => p.Achievements).ToArray();
        var completeDefinitionSet = rows.Length == definitions.Length && byId.Count == rows.Length &&
            definitions.All(a => byId.TryGetValue(a.Id, out var r) &&
                AchievementView.Normalize(a.Name) == AchievementView.Normalize(r.Name));
        // The background marks the whole list, independent of whether DLC groups are known.
        var fullyComplete = aggregateKnown ? total > 0 && unlocked == total :
            completeDefinitionSet && definitions.Length > 0 && definitions.All(Earned);
        return new(baseGame.Length > 0 && (allComplete || baseGame.All(Earned)),
            addOns.Length > 0 && (allComplete || completeDefinitionSet && addOns.All(Earned)), fullyComplete);
    }
}
