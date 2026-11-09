namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    public string BuildVersionLabel => "Achievement Labs " + AppUpdater.CurrentDisplayVersion
#if LABS_UPDATE_TESTING
        + " · PRIVATE UPDATE TEST"
#endif
        ;
}
