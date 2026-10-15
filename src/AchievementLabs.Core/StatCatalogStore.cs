using Newtonsoft.Json.Linq;
using System.Text;

namespace AchievementLabs.Core;

/// <summary>Separates local stat recipes from the achievement catalog without losing edits.</summary>
public static class StatCatalogStore
{
    public static int MigrateLegacy(string directory)
    {
        var dataPath = Path.Combine(directory, "Data.json");
        var statsPath = Path.Combine(directory, "Stats.json");
        if (!File.Exists(dataPath)) return 0;
        var dataOriginal = File.ReadAllText(dataPath);
        var data = JObject.Parse(dataOriginal);
        var statsOriginal = File.Exists(statsPath) ? File.ReadAllText(statsPath) : null;
        var stats = statsOriginal == null ? new JObject() : JObject.Parse(statsOriginal);
        var moved = 0;
        foreach (var title in data.Properties().ToArray())
        {
            if (!uint.TryParse(title.Name, out _) || title.Value is not JObject block || block["Stats"] is not JObject oldStats) continue;
            if (stats[title.Name] is not null and not JObject) throw new InvalidDataException("Invalid existing stat title block.");
            var target = stats[title.Name] as JObject ?? new JObject();
            if (target["Stats"] is not null and not JObject) throw new InvalidDataException("Invalid existing Stats section; both files were preserved.");
            var newStats = target["Stats"] as JObject ?? new JObject();
            foreach (var stat in oldStats.Properties())
            {
                if (newStats[stat.Name] != null && !JToken.DeepEquals(newStats[stat.Name], stat.Value))
                    throw new InvalidDataException($"Conflicting Stats.json entry for title {title.Name}, stat {stat.Name}. Both files were preserved.");
                newStats[stat.Name] ??= stat.Value.DeepClone();
            }
            target["Stats"] = newStats;
            foreach (var key in new[] { "Name", "TitleName", "ServiceConfigId", "StatsMode", "CandidateReplacementData" })
            {
                if (block[key] == null) continue;
                if (key is "CandidateReplacementData" or "StatsMode" && target[key] != null && !JToken.DeepEquals(target[key], block[key]))
                    throw new InvalidDataException("Conflicting stat candidate metadata; both files were preserved.");
                target[key] ??= block[key]!.DeepClone();
            }
            stats[title.Name] = target;
            block.Remove("Stats"); block.Remove("StatsMode"); block.Remove("CandidateReplacementData");
            moved++;
        }
        if (moved == 0) return 0;
        var supported = data.Property("SupportedTitleIDs");
        var supportedValue = supported?.Value.DeepClone() ?? new JArray();
        supported?.Remove(); data.Add("SupportedTitleIDs", supportedValue);
        if (data.Properties().Last().Name != "SupportedTitleIDs") throw new InvalidDataException("Achievement catalog ordering failed.");
        if (File.ReadAllText(dataPath) != dataOriginal || (File.Exists(statsPath) ? File.ReadAllText(statsPath) : null) != statsOriginal)
            throw new IOException("Catalog changed during migration. Retry after reloading.");
        File.Copy(dataPath, dataPath + ".stats-migration-" + Guid.NewGuid().ToString("N") + ".bak");
        if (statsOriginal != null) File.Copy(statsPath, statsPath + ".stats-migration-" + Guid.NewGuid().ToString("N") + ".bak");
        // Publish stat recipes first. If the second replacement fails, legacy recipes
        // remain in Data.json and the migration can safely be attempted again.
        Replace(statsPath, stats);
        Replace(dataPath, data);
        return moved;
    }

    public static void Replace(string path, JObject data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, data.ToString(Newtonsoft.Json.Formatting.Indented), Encoding.UTF8); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
