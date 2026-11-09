namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    public string BuildVersionLabel => "Achievement Labs " + AppUpdater.CurrentDisplayVersion;
}
