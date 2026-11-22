using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private async void LoadTitleSupportDetails(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Button { Tag: TitleSearchResult title }) await model.LoadTitleSupportDetailsAsync(title);
    }
    private async void ExportConfirmedSupport(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export confirmed unlock methods", SuggestedFileName = "achievement-unlock-methods.json", DefaultExtension = "json" });
        if (file == null) return;
        try { await model.ExportConfirmedSupportAsync(file.Path.LocalPath); }
        catch { model.Notice = "Could not export confirmed support to that file."; }
    }
}
