using System.ComponentModel;

namespace AchievementLabs.Desktop;

public sealed class TitlePlatformFilter(string key, string label, Action changed) : INotifyPropertyChanged
{
    private bool selected = true;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public bool Selected
    {
        get => selected;
        set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); changed(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
