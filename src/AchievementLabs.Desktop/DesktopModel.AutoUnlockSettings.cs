using AchievementLabs.Core;
using AchievementLabs.Desktop.Workflows;
namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private readonly AutoUnlockPreferencesStore autoUnlockPreferencesStore = new(AchievementLabsPaths.LocalFile("auto-unlock-settings.json"));
    private bool autoUnlockPreferencesReady;
    private AutoUnlockerViewModel ObserveAutoUnlockPreferences(AutoUnlockerViewModel runner)
    {
        runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(runner.RefreshTokenOnFailure) or nameof(runner.NotifyOnFailure)
                or nameof(runner.StopOnFailure) or nameof(runner.SpeedMultiplier) or nameof(runner.ReferenceGamertag))
                SaveAutoUnlockPreferences();
        };
        return runner;
    }
    private BuildQueueViewModel ObserveBuildQueuePreferences(BuildQueueViewModel runner)
    {
        runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(runner.SpeedMultiplier) or nameof(runner.ReferenceGamertag)) SaveAutoUnlockPreferences();
        };
        return runner;
    }
    private void LoadAutoUnlockPreferences()
    {
        autoUnlockPreferencesReady = false;
        try
        {
            if (autoUnlockPreferencesStore.Load() is { } value)
            {
                XboxQueue.RefreshTokenOnFailure = value.RefreshTokenOnFailure;
                XboxQueue.NotifyOnFailure = value.NotifyOnFailure;
                XboxQueue.StopOnFailure = value.StopOnFailure;
                XboxQueue.SpeedMultiplier = value.SpeedMultiplier;
                XboxQueue.ReferenceGamertag = value.ReferenceGamertag;
                XboxBuildQueue.SpeedMultiplier = value.BuildSpeedMultiplier;
                XboxBuildQueue.ReferenceGamertag = value.BuildReferenceGamertag;
                BuildQueueBufferSeconds = value.BuildBufferSeconds;
                StopAutoUnlockOnSpoofFailure = value.StopOnSpoofFailure;
                NotifyAutoUnlockCompletion = value.NotifyOnCompletion;
            }
            autoUnlockPreferencesReady = true;
        }
        catch { Notice = "Auto Unlock settings could not be read. The existing file was retained."; }
    }
    private void SaveAutoUnlockPreferences()
    {
        if (!autoUnlockPreferencesReady) return;
        try
        {
            autoUnlockPreferencesStore.Save(new()
            {
                RefreshTokenOnFailure = XboxQueue.RefreshTokenOnFailure, NotifyOnFailure = XboxQueue.NotifyOnFailure,
                StopOnFailure = XboxQueue.StopOnFailure, SpeedMultiplier = XboxQueue.SpeedMultiplier,
                ReferenceGamertag = XboxQueue.ReferenceGamertag, BuildSpeedMultiplier = XboxBuildQueue.SpeedMultiplier,
                BuildReferenceGamertag = XboxBuildQueue.ReferenceGamertag, BuildBufferSeconds = BuildQueueBufferSeconds,
                StopOnSpoofFailure = StopAutoUnlockOnSpoofFailure, NotifyOnCompletion = NotifyAutoUnlockCompletion
            });
        }
        catch { Notice = "Could not save Auto Unlock settings. Check the settings folder is writable."; }
    }
}
