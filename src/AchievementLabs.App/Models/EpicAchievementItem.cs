namespace AchievementLabs.Models;

public partial class EpicAchievementItem : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _state = "EOS credentials required";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _canWrite;
    [ObservableProperty] private bool? _unlocked;
    [ObservableProperty] private string _imageUrl = "";
    [ObservableProperty] private double? _globalPercent;
    [ObservableProperty] private bool _isDefinition = true;
}
