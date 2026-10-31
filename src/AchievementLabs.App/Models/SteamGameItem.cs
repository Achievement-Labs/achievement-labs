using System.IO;

namespace AchievementLabs.Models;

public partial class SteamGameItem : ObservableObject
{
    [ObservableProperty] private long _appId;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _installPath = string.Empty;
    [ObservableProperty] private string _schemaPath = string.Empty;
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private string _imageUrl = "pack://application:,,,/Assets/achievement-labs-icon.png";
    [ObservableProperty] private string _achievementSummary = "Open to load";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private int _earnedAchievements;
    [ObservableProperty] private int _totalAchievements;
    [ObservableProperty] private bool _progressKnown;

    public bool IsComplete => ProgressKnown && TotalAchievements > 0 && EarnedAchievements == TotalAchievements;
    public string CompletionBackground => IsComplete ? "#183A27" : "Transparent";
    public string CompletionLabel => IsComplete ? "100% complete" : "";
    public void ApplyProgress(int earned, int total, bool known = true)
    {
        EarnedAchievements = earned;
        TotalAchievements = total;
        ProgressKnown = known;
        Progress = known && total > 0 ? 100d * earned / total : 0;
        AchievementSummary = known ? $"{earned} / {total} · {Progress:0.#}%" : total > 0 ? $"— / {total} · Unavailable" : "Progress unavailable";
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(CompletionBackground));
        OnPropertyChanged(nameof(CompletionLabel));
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"App {AppId}" : Name;
    public string Monogram => DisplayName[..1].ToUpperInvariant();
    public bool IsInstalled => !string.IsNullOrWhiteSpace(InstallPath) && Directory.Exists(InstallPath);
    public bool HasSchema => !string.IsNullOrWhiteSpace(SchemaPath) && File.Exists(SchemaPath);
    public string Status => IsInstalled ? "Installed" : "Not installed";

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Monogram));
    }

    partial void OnInstallPathChanged(string value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(Status));
    }

    partial void OnSchemaPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasSchema));
        OnPropertyChanged(nameof(Status));
    }
}
