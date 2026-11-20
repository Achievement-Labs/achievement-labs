namespace AchievementLabs.Core;

/// <summary>Finds local title files in flat, indexed, and named split catalogs.</summary>
public static class EventCatalogFileLocator
{
    public static string? Find(string directory, string titleId, string fileName)
    {
        if (!uint.TryParse(titleId, out var id) || id == 0 || titleId != id.ToString() ||
            Path.GetFileName(fileName) != fileName)
            throw new ArgumentException("Invalid title file identity.");
        var root = Path.GetFullPath(directory);
        var numeric = Path.Combine(root, titleId, fileName);
        if (File.Exists(numeric)) return numeric;
        if (Directory.Exists(root))
        {
            var named = Directory.EnumerateDirectories(root, titleId + " - *", SearchOption.TopDirectoryOnly)
                .Select(folder => Path.Combine(folder, fileName)).Where(File.Exists).Take(2).ToArray();
            if (named.Length > 1) throw new InvalidDataException("Multiple local folders contain this title's " + fileName + ".");
            if (named.Length == 1) return named[0];
        }
        var flat = Path.Combine(root, fileName);
        return File.Exists(flat) ? flat : null;
    }
}
