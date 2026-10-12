using System.Text.Json;
namespace AchievementLabs.Core;

public sealed record DesktopPreferences
{
    public string EventsDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AchievementLabs", "Events");
    public string SessionPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AchievementLabs", "auth.json");
    public bool RegionOverride { get; init; }
    public bool UnlockAllEnabled { get; init; }
    public string OAuthProfile { get; init; } = "Xbox App PC";
    public bool FakeSignature { get; init; }
    public bool PrivacyMode { get; init; }
    public bool AutoSpoof { get; init; }
    public bool AutoLaunchXboxApp { get; init; }
    public bool LaunchXboxAppHidden { get; init; }
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
