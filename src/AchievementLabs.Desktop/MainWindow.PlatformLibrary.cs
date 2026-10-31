using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AchievementLabs.Desktop.Platforms;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private async void ShowEa(object? sender, RoutedEventArgs e) => await model.OpenPlatformAsync("EA");
    private async void ShowGog(object? sender, RoutedEventArgs e) => await model.OpenPlatformAsync("GOG");
    private async void ShowEpicAchievements(object? sender, RoutedEventArgs e) => await model.OpenPlatformAsync("Epic", true);
    private async void ShowUbisoftAchievements(object? sender, RoutedEventArgs e) => await model.OpenPlatformAsync("Ubisoft", true);
    private async void ShowEaAchievements(object? sender, RoutedEventArgs e) => await model.OpenPlatformAsync("EA", true);
    private async void ShowGogAchievements(object? sender, RoutedEventArgs e) => await model.OpenPlatformAsync("GOG", true);
    private void DisconnectPlatformAccount(object? sender, RoutedEventArgs e) => model.DisconnectPlatformAccount();
    private void CancelPlatformRefresh(object? sender, RoutedEventArgs e) => model.CancelPlatformRefresh();
    private async void AttachPlatformSession(object? sender, RoutedEventArgs e) => await model.AttachPlatformSessionAsync();
    private async void RefreshPlatformAchievements(object? sender, RoutedEventArgs e) => await model.LoadPlatformAchievementsAsync(true);
    private async void SignInPlatformAccount(object? sender, RoutedEventArgs e)
    {
        if (!model.CanInteract) return;
        using var cancellation = new CancellationTokenSource();
        var dialog = new Window { Title = "Sign in to " + model.PlatformBrand, Width = 440, Height = 220, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "Cancel sign-in" };
        cancel.Click += (_, _) => cancellation.Cancel();
        dialog.Closed += (_, _) => cancellation.Cancel();
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 16, Children = { new TextBlock { Text = "Finish sign-in in the browser window. Your account library will load automatically afterward.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, cancel } };
        var shown = dialog.ShowDialog(this);
        var connected = await model.ConnectPlatformAccountAsync(cancellation.Token);
        dialog.Close(); await shown;
        if (connected) await model.RefreshPlatformLibraryAsync();
    }
    private void PlatformLibrary(object? sender, RoutedEventArgs e) => model.ShowPlatformLibrary();
    private void PlatformAchievements(object? sender, RoutedEventArgs e) => model.ShowPlatformAchievements();
    private async void PlatformTools(object? sender, RoutedEventArgs e) => await model.ShowPlatformToolsAsync();
    private void OpenPlatformGameAchievements(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlatformGameRow game }) model.ShowPlatformAchievements(game);
    }
    private void LaunchPlatformGame(object? sender, RoutedEventArgs e)
    {
        if (!model.CanInteract || sender is not Button { Tag: PlatformGameRow { CanLaunch: true } game }) return;
        OpenPlatformClient(game);
    }
    private void OpenPlatformClient(PlatformGameRow game)
    {
        if (!Uri.TryCreate(game.LaunchUri, UriKind.Absolute, out var uri) || uri.Scheme is not ("origin2" or "goggalaxy" or "com.epicgames.launcher" or "uplay")) { model.Notice = "This title has no supported launcher route."; return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });
            model.Notice = game.Platform == "GOG" ? "Requested the game view in Galaxy." : "Requested game launch through the platform client.";
        }
        catch { model.Notice = "The platform client could not open this title. Check that the client is installed and signed in."; }
    }
    private async void PlatformPresence(object? sender, RoutedEventArgs e)
    {
        var game = sender is Button { Tag: PlatformGameRow tagged } ? tagged : model.CurrentPlatformGame;
        if (game == null) { model.Notice = "Choose a title first."; return; }
        var dialog = new Window { Title = $"{game.Platform} presence / spoof", Width = 500, Height = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var launch = new Button { Content = "Open in client", IsEnabled = game.CanLaunch };
        launch.Click += (_, _) => OpenPlatformClient(game);
        var close = new Button { Content = "Close" }; close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14, Children = {
            new TextBlock { Text = game.Name, FontSize = 22, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = "Standalone spoofing is not available for this platform in this test build. An authenticated, title-specific adapter is required before presence can be changed independently of a game.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = game.Platform == "GOG" ? "Open in client opens the game's Galaxy page. Start the game in Galaxy for normal presence and playtime tracking." : "Open in client requests the actual game launch. This can start the game; it is not a spoof session.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { launch, close } }
        } };
        await dialog.ShowDialog(this);
    }
    private async void ImportPlatformSnapshot(object? sender, RoutedEventArgs e)
    {
        if (!model.CanInteract) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import platform achievement snapshot", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Achievement snapshot") { Patterns = ["*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await model.ImportPlatformSnapshotAsync(path);
    }
    private async void ExportPlatformCsv(object? sender, RoutedEventArgs e)
    {
        var rows = model.PlatformAchievementRows.ToArray();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export platform achievements", SuggestedFileName = $"{model.ActivePlatform}-achievements.csv", DefaultExtension = "csv" });
        if (file == null) return;
        static string Cell(string value) { if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
        try
        {
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true));
            await writer.WriteLineAsync("ID,Name,Description,Status,Global %,Rarity,Unlock time,Source");
            foreach (var row in rows) await writer.WriteLineAsync(string.Join(",", new[] { row.Id, row.Name, row.Description, row.Status, row.GlobalLabel, row.Rarity, row.UnlockTime, row.Source }.Select(Cell)));
        }
        catch { model.Notice = "The export could not be saved. Check the destination and try again."; }
    }
}
