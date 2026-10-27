namespace AchievementLabs.Core;

public static class AchievementLabsPaths
{
    private const string CurrentFolder = "AchievementLabs";
    private const string LegacyFolder = "UltimateAchievementLabs";

    public static string LocalFile(string fileName)
    {
        var local = OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(local, CurrentFolder, fileName);
        var legacy = Path.Combine(local, LegacyFolder, fileName);
        if (!File.Exists(current) && File.Exists(legacy))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            File.Copy(legacy, current, false);
        }
        return current;
    }
}
