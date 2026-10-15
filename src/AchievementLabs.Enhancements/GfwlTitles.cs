namespace AchievementLabs.MultiSelect;

// Verified exact Title IDs. Shared console/PC lists are deliberately labelled as shared.
public static class GfwlTitles
{
    static readonly HashSet<string> Exclusive = new(StringComparer.Ordinal) { "1297287434", "1464995744", "1396901887", "1414793308", "1128466446", "1112737750", "1162676180", "1129121839", "1414793275", "1297287183", "1414596639", "1396901867", "1096157225", "1414596663", "1128466423", "1128468385", "1279330283", "1480657387", "1297287233" };
    static readonly HashSet<string> Shared = new(StringComparer.Ordinal) { "1096288213", "1297287425", "1128466398", "1297287126" };
    public static bool IsExclusive(string id) => Exclusive.Contains(id);
    public static bool Supports(string id) => Exclusive.Contains(id) || Shared.Contains(id);
    public static string Label(string id, string original) => IsExclusive(id) ? "GFWL (PC)" : Shared.Contains(id) ? "Xbox 360 / GFWL (shared list)" : original;
}
