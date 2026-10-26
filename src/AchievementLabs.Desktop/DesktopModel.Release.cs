namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    public string BuildVersionLabel => "Achievement Labs " +
        (typeof(DesktopModel).Assembly.GetName().Version?.ToString() ?? "Unknown version");
}
