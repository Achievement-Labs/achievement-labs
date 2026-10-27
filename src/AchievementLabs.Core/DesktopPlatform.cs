namespace AchievementLabs.Core;

public static class DesktopPlatform
{
    public static bool IsUnixDesktop => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
    public static string Name => OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "Mac" : "Windows";
}
