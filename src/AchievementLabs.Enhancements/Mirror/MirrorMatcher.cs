namespace AchievementLabs.Mirror;

/// <summary>
/// Pairs Steam and Xbox achievements by name. Only an exact, unambiguous name match is accepted
/// automatically; anything less certain is left for the user to confirm, because an Xbox unlock
/// can't be undone.
/// </summary>
public static class MirrorMatcher
{
    public const double SuggestThreshold = 0.8;

    /// <summary>
    /// Builds one row per source achievement. Decisions the user already made (confirmed pairs and
    /// "no equivalent") are kept when both achievements still exist.
    /// </summary>
    public static List<MirrorMatch> Build(MirrorDirection direction, IReadOnlyList<MirrorAchievement> steam,
        IReadOnlyList<MirrorAchievement> xbox, IEnumerable<MirrorMatch>? previous = null)
    {
        var source = direction == MirrorDirection.SteamToXbox ? steam : xbox;
        var target = direction == MirrorDirection.SteamToXbox ? xbox : steam;
        var targetById = target.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var targetByName = target.GroupBy(a => Normalize(a.Name)).ToDictionary(g => g.Key, g => g.ToArray());
        var kept = (previous ?? [])
            .Where(m => m.State is MirrorMatchState.Confirmed or MirrorMatchState.Ignored)
            .GroupBy(m => m.SourceId(direction))
            .ToDictionary(g => g.Key, g => g.First());

        var rows = new List<MirrorMatch>();
        foreach (var achievement in source)
        {
            var row = direction == MirrorDirection.SteamToXbox
                ? new MirrorMatch { SteamId = achievement.Id, SteamName = achievement.Name }
                : new MirrorMatch { XboxId = achievement.Id, XboxName = achievement.Name };

            if (kept.TryGetValue(achievement.Id, out var decision))
            {
                var keptTarget = decision.TargetId(direction);
                if (decision.State == MirrorMatchState.Ignored) { row.State = MirrorMatchState.Ignored; rows.Add(row); continue; }
                if (!string.IsNullOrEmpty(keptTarget) && targetById.TryGetValue(keptTarget, out var stillThere))
                {
                    SetTarget(row, direction, stillThere); row.State = MirrorMatchState.Confirmed; rows.Add(row); continue;
                }
            }

            var key = Normalize(achievement.Name);
            if (key.Length > 0 && targetByName.TryGetValue(key, out var exact))
            {
                SetTarget(row, direction, exact[0]);
                // Two target achievements with the same name: never guess, ask.
                row.State = exact.Length == 1 && SourceNameIsUnique(source, key) ? MirrorMatchState.Matched : MirrorMatchState.Suggested;
            }
            else if (BestClose(achievement.Name, target) is { } close)
            {
                SetTarget(row, direction, close);
                row.State = MirrorMatchState.Suggested;
            }
            rows.Add(row);
        }
        return rows;
    }

    public static void SetTarget(MirrorMatch row, MirrorDirection direction, MirrorAchievement? target)
    {
        if (direction == MirrorDirection.SteamToXbox) { row.XboxId = target?.Id; row.XboxName = target?.Name ?? ""; }
        else { row.SteamId = target?.Id ?? ""; row.SteamName = target?.Name ?? ""; }
    }

    private static bool SourceNameIsUnique(IReadOnlyList<MirrorAchievement> source, string key) => source.Count(a => Normalize(a.Name) == key) == 1;

    private static MirrorAchievement? BestClose(string name, IReadOnlyList<MirrorAchievement> target)
    {
        var key = Normalize(name);
        if (key.Length < 4) return null;
        var scored = target.Select(a => (Achievement: a, Score: Similarity(key, Normalize(a.Name))))
            .Where(x => x.Score >= SuggestThreshold).OrderByDescending(x => x.Score).Take(2).ToArray();
        if (scored.Length == 0) return null;
        // A tie means the name isn't distinctive enough to suggest anything.
        if (scored.Length == 2 && Math.Abs(scored[0].Score - scored[1].Score) < 0.0001) return null;
        return scored[0].Achievement;
    }

    /// <summary>Lower-case letters and digits only, so punctuation, spacing and case don't block a match.</summary>
    public static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>1 − (edit distance / longer length).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
}
