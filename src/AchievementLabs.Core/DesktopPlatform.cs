namespace AchievementLabs.Core;

public static class DesktopPlatform
{
    public static bool IsUnixDesktop => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
    public static string Name => OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "Mac" : "Windows";
    public static string DocumentsDirectory
    {
        get
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return OperatingSystem.IsLinux() && !Path.IsPathFullyQualified(path)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents") : path;
        }
    }
}
