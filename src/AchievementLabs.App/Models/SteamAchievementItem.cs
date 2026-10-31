namespace AchievementLabs.Models;

public partial class SteamAchievementItem : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private bool _desiredUnlocked;
    [ObservableProperty] private DateTime? _unlockTime;
    [ObservableProperty] private int _permission;
    [ObservableProperty] private string _iconUrl = "";
    [ObservableProperty] private string _lockedIconUrl = "";
    [ObservableProperty] private double? _globalPercent;
    public string ImageUrl => IsUnlocked || string.IsNullOrWhiteSpace(LockedIconUrl) ? IconUrl : LockedIconUrl;
    public string Status => IsUnlocked ? "Unlocked" : "Locked";
    public string GlobalUnlockLabel => GlobalPercent is { } percent ? $"{percent:0.##}%" : "Unavailable";
    public string Rarity => GlobalPercent is { } percent ? percent <= 5 ? "Rare" : "Common" : "Unavailable";
    public string UnlockTimeLabel => UnlockTime?.ToString("g") ?? "—";
    partial void OnGlobalPercentChanged(double? value) { OnPropertyChanged(nameof(GlobalUnlockLabel)); OnPropertyChanged(nameof(Rarity)); }
    partial void OnIconUrlChanged(string value) => OnPropertyChanged(nameof(ImageUrl));
    partial void OnLockedIconUrlChanged(string value) => OnPropertyChanged(nameof(ImageUrl));
    partial void OnUnlockTimeChanged(DateTime? value) => OnPropertyChanged(nameof(UnlockTimeLabel));

    public bool IsModified => IsUnlocked != DesiredUnlocked;

    partial void OnDesiredUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsModified));
    }

    partial void OnIsUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ImageUrl));
    }
}
