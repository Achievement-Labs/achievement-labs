namespace AchievementLabs.Core;

public static class LibrarySortPreferences
{
    public static string Normalize(string? sort) => sort is "Z-A" or "Last Played" ? sort : "A-Z";
    public static string Load(string path) => File.Exists(path) ? Normalize(File.ReadAllText(path).Trim()) : "A-Z";
    public static void Save(string path, string sort)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, Normalize(sort)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
