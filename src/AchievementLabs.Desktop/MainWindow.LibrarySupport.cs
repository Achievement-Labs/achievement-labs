using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private async void CheckLibrarySupport(object? sender, RoutedEventArgs e) => await model.CheckLibrarySupportAsync();
    private void StopLibrarySupport(object? sender, RoutedEventArgs e) => model.StopLibrarySupportCheck();
    private async void ExportConfirmedSupport(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export confirmed unlock methods", SuggestedFileName = "achievement-unlock-methods.json", DefaultExtension = "json" });
        if (file == null) return;
        try { await model.ExportConfirmedSupportAsync(file.Path.LocalPath); }
        catch { model.Notice = "Could not export confirmed support to that file."; }
    }
}
