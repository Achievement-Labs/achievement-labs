using System.Text.Json;
namespace AchievementLabs.Core;

public sealed record DesktopPreferences
{
    public string EventsDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AchievementLabs", "Events");
    public string SessionPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AchievementLabs", "auth.json");
    public bool RememberWindowSize { get; init; }
    public bool AutoMaximize { get; init; }
    public bool RegionOverride { get; init; }
    public bool UnlockAllEnabled { get; init; }
    public string OAuthProfile { get; init; } = "Xbox App PC";
    public bool? RememberMacSignIn { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? RememberLinuxSignIn { get; init; }
    public bool FakeSignature { get; init; }
    public bool PrivacyMode { get; init; }
    public bool AutoSpoof { get; init; }
    public double BuildQueueBufferSeconds { get; init; } = 60;
    public bool AutoLaunchXboxApp { get; init; }
    public bool LaunchXboxAppHidden { get; init; }
    public bool WindowsNotificationsEnabled { get; init; } = true;
    public bool StopAutoUnlockOnSpoofFailure { get; init; }
    public bool NotifyAutoUnlockCompletion { get; init; } = true;
    public bool NotifySpooferStops { get; init; } = true;
    public bool AutoRefreshEvents { get; init; } = true;
    public bool AutoRefreshDlc { get; init; } = true;
    public bool ShowMythicIcon { get; init; } = true;
    public bool HighlightCompletedDlcs { get; init; } = true;
    public string MythicColour { get; init; } = "#70C98A";
    public string CompletedDlcColour { get; init; } = "#183A27";
    public bool MintAccent { get; init; }
    public bool AutomaticallyCheckUpdates { get; init; } = true;
}
public sealed class DesktopPreferencesStore(string path)
{
    public async Task<DesktopPreferences> LoadAsync(CancellationToken cancellationToken = default) => File.Exists(path)
        ? JsonSerializer.Deserialize<DesktopPreferences>(await File.ReadAllTextAsync(path, cancellationToken)) ?? throw new InvalidDataException("Settings are empty.")
        : new();
    public async Task SaveAsync(DesktopPreferences preferences, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(preferences.EventsDirectory) || !Path.IsPathFullyQualified(preferences.SessionPath))
            throw new InvalidDataException("Choose absolute paths for the Events folder and saved session.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
