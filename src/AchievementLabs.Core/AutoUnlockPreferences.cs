using System.Text.Json;
namespace AchievementLabs.Core;

// Preferences are independent of any game's progress checkpoint.
public sealed record AutoUnlockPreferences
{
    public bool RefreshTokenOnFailure { get; init; }
    public bool NotifyOnFailure { get; init; } = true;
    public bool StopOnFailure { get; init; }
    public bool StopOnSpoofFailure { get; init; }
    public bool NotifyOnCompletion { get; init; } = true;
    public double SpeedMultiplier { get; init; } = 1;
    public double BuildSpeedMultiplier { get; init; } = 1;
    public double BuildBufferSeconds { get; init; } = 60;
    public string ReferenceGamertag { get; init; } = "";
    public string BuildReferenceGamertag { get; init; } = "";
    public void Validate()
    {
        if (!double.IsFinite(SpeedMultiplier) || SpeedMultiplier is < 0.1 or > 100 ||
            !double.IsFinite(BuildSpeedMultiplier) || BuildSpeedMultiplier is < 0.1 or > 100 ||
            !double.IsFinite(BuildBufferSeconds) || BuildBufferSeconds is < 0 or > 3600)
            throw new InvalidDataException("Auto Unlock settings are outside the supported range.");
    }
}
public sealed class AutoUnlockPreferencesStore(string path)
{
    public AutoUnlockPreferences? Load()
    {
        if (!File.Exists(path)) return null;
        var value = JsonSerializer.Deserialize<AutoUnlockPreferences>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Auto Unlock settings are empty.");
        value.Validate(); return value;
    }
    public void Save(AutoUnlockPreferences value)
    {
        value.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
