using Avalonia.Controls;
using Avalonia.Interactivity;
using AchievementLabs.Models;
using Avalonia;
using Avalonia.VisualTree;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private async void ConfigureSteamAccount(object? sender, RoutedEventArgs e)
    {
        var key = new TextBox { PasswordChar = '●', PlaceholderText = "Steam Web API key (stored encrypted)" };
        var dialog = new Window { Title = "Steam account library access", Width = 480, Height = 290, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var save = new Button { Content = "Save and refresh" };
        save.Click += (_, _) =>
        {
            var value = key.Text?.Trim() ?? "";
            if (value.Length != 32 || !value.All(Uri.IsHexDigit)) { model.Notice = "Enter a valid Steam Web API key."; return; }
            try { AchievementLabs.Core.MirrorStore.SaveKey(value); key.Text = ""; dialog.Close(true); }
            catch { model.Notice = "The Steam Web API key could not be saved."; }
        };
        var website = new Button { Content = "Open Steam key page" };
        website.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://steamcommunity.com/dev/apikey") { UseShellExecute = true });
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children = { new TextBlock { Text = "Steam uses the account signed in to the local client. Add your Web API key to read its full library and achievements for uninstalled games, when the public game list is unavailable.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, key, new WrapPanel { Children = { save, website } } } };
        if (await dialog.ShowDialog<bool>(this)) await model.RefreshSteamAsync();
    }
    private async Task CheckSteamUiAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var output = args.SkipWhile(a => a != "--output").Skip(1).First();
        Directory.CreateDirectory(output);
        try
        {
            model.LoadSteamPreview();
            model.CheckSteamPresentation();
            model.AddSteamLayoutFixtures();
            var root = this.FindControl<Grid>("PreviewRoot")!;
            foreach (var width in new[] { 1440, 1120, 1000, 900 })
            {
                Width = width; Height = width == 1440 ? 920 : width == 900 ? 650 : 720;
                foreach (var page in new[] { "SteamLibrary", "SteamAchievements" })
                {
                    model.Navigate(page);
                    await Task.Delay(250);
                    if (page == "SteamLibrary")
                    {
                        var library = this.FindControl<ListBox>("SteamLibraryList")!;
                        foreach (var button in library.GetVisualDescendants().OfType<Button>().Where(b => b.Content is "Open achievements" or "Open spoof"))
                        {
                            var origin = button.TranslatePoint(new Point(), library) ?? throw new Exception("Steam action has no layout position");
                            if (origin.X + button.Bounds.Width > library.Bounds.Width || origin.X < 0) throw new Exception($"Clipped Steam library action at width {width}");
                        }
                    }
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height), new Avalonia.Vector(96, 96));
                    bitmap.Render(root);
                    bitmap.Save(Path.Combine(output, $"{page}-{width}.png"));
                }
            }
            var list = this.FindControl<ListBox>("SteamAchievementList")!;
            SelectVisibleSteam(null, new RoutedEventArgs());
            if (list.SelectedItems?.Count != 3) throw new Exception("Steam select all visible");
            ClearSteamSelection(null, new RoutedEventArgs());
            if (list.SelectedItems?.Count != 0) throw new Exception("Steam clear selection");
            model.LoadXboxLayoutPreview();
            foreach (var width in new[] { 1120, 900 })
            {
                Width = width; Height = 720;
                await Task.Delay(200);
                var xbox = this.FindControl<ListBox>("XboxLibraryList")!;
                foreach (var button in xbox.GetVisualDescendants().OfType<Button>())
                {
                    var origin = button.TranslatePoint(new Point(), xbox)!.Value;
                    if (origin.X < 0 || origin.X + button.Bounds.Width > xbox.Bounds.Width) throw new Exception("Xbox library actions overflow their viewport");
                }
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height), new Vector(96, 96));
                bitmap.Render(root); bitmap.Save(Path.Combine(output, $"XboxLibrary-{width}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            File.WriteAllText(Path.Combine(output, "steam-ui-check.txt"), "PASS: Steam completion/zero/unknown states, sorting/search/filters and selection; long-title scrolling lists and action bounds at 1440/1120/1000/900; compact Xbox library action bounds. No unlocks or spoof sessions.");
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "steam-ui-error.txt"), error.ToString()); Environment.ExitCode = 1; }
        Close();
    }

    private void ToggleSteamMultiple(object? sender, RoutedEventArgs e)
    {
        var list = this.FindControl<ListBox>("SteamAchievementList")!;
        list.SelectionMode = list.SelectionMode == SelectionMode.Single ? SelectionMode.Multiple | SelectionMode.Toggle : SelectionMode.Single;
        if (sender is Button button) button.Content = list.SelectionMode == SelectionMode.Single ? "Select multiple" : "Select single";
    }

    private void SelectVisibleSteam(object? sender, RoutedEventArgs e)
    {
        var list = this.FindControl<ListBox>("SteamAchievementList")!;
        list.SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle;
        this.FindControl<Button>("SteamMultipleButton")!.Content = "Select single";
        list.SelectAll();
    }

    private void ClearSteamSelection(object? sender, RoutedEventArgs e) => this.FindControl<ListBox>("SteamAchievementList")!.UnselectAll();

    private async void UnlockSteamSelected(object? sender, RoutedEventArgs e)
    {
        var list = this.FindControl<ListBox>("SteamAchievementList")!;
        await model.UnlockSteamSelectionAsync(list.SelectedItems?.OfType<SteamAchievementItem>().ToArray() ?? []);
    }

    private async void UnlockSteamAll(object? sender, RoutedEventArgs e) => await model.UnlockSteamSelectionAsync(model.SteamAchievements.Where(a => !a.IsUnlocked).ToArray());

    private async void FillSteamTotals(object? sender, RoutedEventArgs e) => await model.FillSteamTotalsAsync();

    private void OpenSteamAutoUnlock(object? sender, RoutedEventArgs e)
    {
        model.SteamQueue.AppId = model.SteamAppId;
        model.Navigate("SteamQueue");
    }

    private async void ExportSteamCsv(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions { Title = "Export Steam achievements", SuggestedFileName = $"steam-{model.SteamAppId}.csv", DefaultExtension = "csv" });
        if (file == null) return;
        static string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true));
            await writer.WriteLineAsync("ID,Name,Description,Status,Global %,Rarity,Unlock time");
            foreach (var row in model.VisibleSteamAchievements)
                await writer.WriteLineAsync(string.Join(",", new[] { row.Id, row.Name, row.Description, row.Status, row.GlobalUnlockLabel, row.Rarity, row.UnlockTimeLabel }.Select(Cell)));
        }
        catch { model.Notice = "Could not export Steam achievements. Check the destination and try again."; }
    }

    private async void OpenSteamSpoof(object? sender, RoutedEventArgs e)
    {
        if (!model.CanInteract) return;
        if (sender is Button { Tag: SteamGameItem game }) model.SelectedSteamGame = game;
        var dialog = new Window { Title = "Steam title spoof", Width = 460, Height = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner, DataContext = model };
        var start = new Button { Content = "Start spoof" };
        var stop = new Button { Content = "Stop spoof" };
        start.Click += async (_, _) => { start.IsEnabled = false; try { await model.StartSteamPresenceAsync(); } finally { start.IsEnabled = true; } };
        stop.Click += (_, _) => model.StopSteamPresence();
        var appId = new TextBox { PlaceholderText = "Steam app ID" };
        appId.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(DesktopModel.SteamAppId)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(DesktopModel.SteamStatus)));
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 12, Children = { new TextBlock { Text = "Steam title spoof", FontSize = 22 }, appId, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { start, stop } }, status } };
        await dialog.ShowDialog(this);
    }
}
