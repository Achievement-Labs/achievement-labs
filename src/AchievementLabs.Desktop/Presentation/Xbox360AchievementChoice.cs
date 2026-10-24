using System.ComponentModel;
using AchievementLabs.Models;

namespace AchievementLabs.Desktop;

public sealed class Xbox360AchievementChoice(Xbox360ProfileAchievement achievement, Action changed) : INotifyPropertyChanged
{
    public Xbox360ProfileAchievement Achievement { get; } = achievement;
    public string Name => Achievement.Name;
    public string Description => Unlocked && !string.IsNullOrWhiteSpace(Achievement.UnlockedDescription) ? Achievement.UnlockedDescription : Achievement.LockedDescription;
    public string Status => Achievement.Status;
    public bool Unlocked => (Achievement.Flags & 0x20000u) != 0;
    public string RowBackground => Unlocked ? "#2631A45D" : "Transparent";
    public string RowForeground => Unlocked ? "#91DEB0" : "#E2E9EA";
    public string StatusForeground => Unlocked ? "#91DEB0" : "#ADBFC4";
    public uint Gamerscore => Achievement.Gamerscore;
    private bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
