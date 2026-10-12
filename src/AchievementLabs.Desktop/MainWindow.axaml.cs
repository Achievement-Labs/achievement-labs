using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
namespace AchievementLabs.Desktop;
public partial class MainWindow : Window
{
    private readonly DesktopModel model = new();
    private readonly DispatcherTimer xboxPresenceTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent(); DataContext = model; model.EventTokenRequired = ShowEventTokenRequiredAsync;
        Workflows.NativeClipboard.WriteAsync = async text => { try { if (Clipboard != null) await Clipboard.SetTextAsync(text); } catch { model.Notice = "Could not copy to the clipboard."; } };
        xboxPresenceTimer.Tick += (_, _) => model.RefreshXboxPcAppPresence();
        xboxPresenceTimer.Start();
        Closed += (_, _) => { xboxPresenceTimer.Stop(); model.Dispose(); };
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.MintAccent)) ApplyAccent(); };
        Opened += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--release-smoke-test"))
            {
                var releaseArgs = Environment.GetCommandLineArgs();
                var reportDirectory = releaseArgs.SkipWhile(a => a != "--output").Skip(1).First();
                Directory.CreateDirectory(reportDirectory);
                try
                {
                    foreach (var relative in new[] { "steam-idle/AchievementLabs.SteamIdle.exe", "gfwl/AchievementLabs.GfwlInjector.exe", "gfwlam/engine.dll" })
                        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, relative))) throw new Exception("Missing extracted helper: " + relative);
                    if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Events"))) throw new Exception("Private Events directory found in release.");
                    model.ToggleApiServer();
                    if (model.ServerAddress != "Disabled") throw new Exception("Legacy API remains enabled.");
                    model.Navigate("Stats");
                    if (!model.IsStats) throw new Exception("Free feature navigation failed.");
                    model.Navigate("Home");
                    await Task.Delay(150);
                    var releaseRoot = this.FindControl<Grid>("PreviewRoot")!;
                    using var releaseBitmap = new RenderTargetBitmap(new PixelSize((int)releaseRoot.Bounds.Width, (int)releaseRoot.Bounds.Height), new Vector(96, 96));
                    releaseBitmap.Render(releaseRoot);
                    releaseBitmap.Save(Path.Combine(reportDirectory, "release-home.png"), new PngBitmapEncoderOptions());
                    File.WriteAllText(Path.Combine(reportDirectory, "release-smoke.txt"), "PASS: bundled application startup, helper extraction, private Events absent, free feature navigation, native home rendering. No account or payment calls.");
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(reportDirectory, "release-smoke-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                Close();
                return;
            }
            if (!Environment.GetCommandLineArgs().Contains("--smoke-test")) {
                var health = Environment.GetCommandLineArgs().SkipWhile(a => a != "--update-health").Skip(1).FirstOrDefault();
                if (health != null && AppUpdater.IsUpdatePath(health)) File.WriteAllText(health, "Ready");
                await model.LoadPreferencesAsync(); _ = model.CheckForUpdatesAsync(); await model.AttachXboxPcAppAsync(); return;
            }
            try
            {
                var args = Environment.GetCommandLineArgs();
                var fixture = args.SkipWhile(a => a != "--fixture").Skip(1).First();
                var output = args.SkipWhile(a => a != "--output").Skip(1).First();
                Directory.CreateDirectory(output);
                await OfflineChecks.RunAsync(output);
                await model.OpenExportAsync(fixture);
                model.Filter("Locked"); if (model.VisibleAchievements.Any(a => a.Unlocked)) throw new Exception("Locked filter");
                model.Filter("Unlocked"); if (model.VisibleAchievements.Any(a => !a.Unlocked)) throw new Exception("Unlocked filter");
                model.Search = "no-match-829519"; if (!model.NoResults || model.HasSelection) throw new Exception("Search empty state");
                model.Search = ""; model.Filter("All");
                await Task.Delay(500);
                var root = this.FindControl<Grid>("PreviewRoot")!;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height), new Vector(96,96));
                bitmap.Render(root); bitmap.Save(Path.Combine(output, "native-achievements.png"), new PngBitmapEncoderOptions());
                model.Navigate("Library"); await Task.Delay(100); bitmap.Render(root); bitmap.Save(Path.Combine(output, "native-library.png"), new PngBitmapEncoderOptions());
                                model.Navigate("Settings"); await Task.Delay(150); bitmap.Render(root); bitmap.Save(Path.Combine(output, "native-settings.png"), new PngBitmapEncoderOptions());
                model.EventsDirectory = Path.GetFullPath("src/AchievementLabs.App/Events"); await model.InspectCatalogAsync(); model.Navigate("Diagnostics"); await Task.Delay(150); bitmap.Render(root); bitmap.Save(Path.Combine(output, "native-catalog.png"), new PngBitmapEncoderOptions());
                foreach (var page in new[] { "SteamLibrary", "SteamAchievements", "Epic", "Ubisoft", "Stats", "Legacy", "Gfwl", "Queues", "Tools" })
                {
                    model.Navigate(page); await Task.Delay(100); bitmap.Render(root); bitmap.Save(Path.Combine(output, "native-" + page.ToLowerInvariant() + ".png"), new PngBitmapEncoderOptions());
                }
                Width = 1120; Height = 720; model.Navigate("Settings"); await Task.Delay(150);
                using (var compact = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height), new Vector(96,96))) { compact.Render(root); compact.Save(Path.Combine(output, "native-compact.png"), new PngBitmapEncoderOptions()); }
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "PresentationFramework" or "WPF-UI")) throw new Exception("WPF UI assembly loaded into native desktop.");
                File.WriteAllText(Path.Combine(output, "smoke-test.txt"), "PASS: real 43-row import and synthetic 10,000-row import/filter/search; native rendering; preferences and protected sessions; catalog ordering/missing templates; known and unknown progress mapping; settings/catalog navigation. No account calls.");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            Close();
        };
    }
    private void GoBack(object? s, RoutedEventArgs e) => model.GoBack();
    private void ShowAbout(object? s, RoutedEventArgs e) => model.Navigate("About");
    private void ShowLibrary(object? s, RoutedEventArgs e) => model.Navigate("Library");
    private void ShowXbox360Profiles(object? s, RoutedEventArgs e) => model.OpenXbox360Profiles();
    private void ShowGfwl(object? s, RoutedEventArgs e) => model.OpenGfwlWorkspace();
    private void OpenGpdFolder(object? s, RoutedEventArgs e) => model.OpenGpdFolder();
    private async void AddXbox360Gpd(object? s, RoutedEventArgs e) => await model.AddXbox360GameAsync(model.GpdTitleId);
    private async void AddTitleSearchGpd(object? s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not TitleSearchResult result || !result.CanAddXbox360Gpd) return;
        if (!uint.TryParse(result.TitleId, out var id)) { model.Notice = "The title search result has an invalid Xbox 360 ID."; return; }
        await model.AddXbox360GameAsync(id.ToString("X8"), result.Name);
    }
    private async void ChooseWindows8State(object? s, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select current XCT bridge_state.json", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Bridge state") { Patterns = ["bridge_state.json"] }] });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) model.Windows8.BridgeStatePath = path;
    }
    private async void ShowHome(object? s, RoutedEventArgs e) => await model.RefreshHomeProfilesAsync();
    private void ShowAchievements(object? s, RoutedEventArgs e) => model.Navigate("Achievements");
    private void ShowSpoofer(object? s, RoutedEventArgs e) => model.OpenSpoofer();
    private async void ChooseGame(object? s, RoutedEventArgs e)
    {
        if (s is not Button { Tag: Game game }) return;
        try
        {
            if (await model.IsGfwlTitleAsync(game)) model.OpenGfwlTitle(game);
            else if (DesktopModel.HasDevice(game, "Xbox360")) await model.OpenXbox360TitleAsync(game);
            else await model.SelectGameAsync(game);
        }
        catch (OperationCanceledException) { }
    }
    private async void OpenSelectedXboxGame(object? s, RoutedEventArgs e)
    {
        var game = (s as Button)?.Tag as Game ?? model.SelectedLibraryGame;
        if (game == null) return;
        if (await model.IsGfwlTitleAsync(game)) { model.OpenGfwlTitle(game); return; }
        if (DesktopModel.HasDevice(game, "Xbox360")) { await model.OpenXbox360TitleAsync(game); return; }
        try { await model.SelectGameAsync(game); } catch (OperationCanceledException) { }
    }
    private async void CopyTitleId(object? s, RoutedEventArgs e)
    {
        var game = (s as Button)?.Tag as Game ?? model.SelectedLibraryGame;
        if (game == null || Clipboard == null) return;
        await Clipboard.SetTextAsync(game.Id);
        model.Notice = $"Copied title ID {game.Id}.";
    }
    private async void CopySearchTitleId(object? s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not TitleSearchResult result || Clipboard == null) return;
        await Clipboard.SetTextAsync(result.TitleId);
        model.Notice = $"Copied title ID {result.TitleId}.";
    }
    private async void OpenSearchTitle(object? s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not TitleSearchResult result || !model.CanQuery) return;
        var known = model.Games.FirstOrDefault(game => game.Id == result.TitleId);
        var game = known ?? new Game(result.TitleId, result.Name, result.Platform == "GFWL" ? "GFWL" : DesktopModel.IsKnownWindows8Title(result.TitleId) ? "Windows8" : result.CanAddXbox360Gpd ? "Xbox360" : "XboxOne", 0, 0, 0, false, result.ImageUrl);
        if (await model.IsGfwlTitleAsync(game)) { model.OpenGfwlTitle(game); return; }
        await model.SelectGameAsync(game);
    }
    private void FilterAll(object? s, RoutedEventArgs e) => model.Filter("All");
    private void FilterLocked(object? s, RoutedEventArgs e) => model.Filter("Locked");
    private void FilterUnlocked(object? s, RoutedEventArgs e) => model.Filter("Unlocked");
    private async void ConnectXbox(object? s, RoutedEventArgs e) => await model.ConnectAsync();
    private async void LaunchXboxAndAttach(object? s, RoutedEventArgs e) => await model.LaunchXboxPcAppAndAttachAsync();
    private async void PasteAndCompleteBrowserLogin(object? s, RoutedEventArgs e)
    {
        if (Clipboard == null) return;
        var redirect = await Clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(redirect)) { model.Notice = "Copy the final login.live.com address first."; return; }
        model.LoginRedirect = redirect.Trim();
        await model.CompleteBrowserLoginAsync();
    }
    private async void ConnectSavedOAuth(object? s, RoutedEventArgs e) => await model.ConnectSavedOAuthFallbackAsync();
    private async void OpenExport(object? s, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open Xbox achievement export", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
        if (files.Count == 0) return;
        try { await model.OpenExportAsync(files[0].TryGetLocalPath() ?? throw new InvalidDataException()); }
        catch { model.Notice = "Could not open this achievement export. Choose an Xbox achievements response JSON file."; }
    }
    private async void RefreshAccountProfile(object? s, RoutedEventArgs e) => await model.RefreshAccountProfileAsync();
    private void ShowSteamProfile(object? s, RoutedEventArgs e) => model.ShowSteamProfile();
    private void ShowTools(object? s, RoutedEventArgs e) => model.Navigate("Tools");
    private async void SearchTitles(object? s, RoutedEventArgs e) => await model.SearchTitlesAsync();
    private async void SearchGamertag(object? s, RoutedEventArgs e) => await model.SearchGamertagAsync();
    private void OpenUtilityLink(object? s, RoutedEventArgs e) { if (s is Button { Tag: string destination }) model.OpenUtilityLink(destination); }
    private void RefreshConverter(object? s, RoutedEventArgs e) => model.RefreshConverterStatus();
    private async void ExportProfileGames(object? s, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export profile games", SuggestedFileName = "games.csv", DefaultExtension = "csv" });
        if (file?.TryGetLocalPath() is { } path) await model.ExportProfileGamesAsync(path);
    }
    private void ShowQueues(object? s, RoutedEventArgs e) => model.OpenQueues();
    private async void RunXboxQueue(object? s, RoutedEventArgs e) => await model.RunXboxQueueAsync();
    private async void RunSteamQueue(object? s, RoutedEventArgs e) => await model.RunSteamQueueAsync();
    private void ShowLegacy(object? s, RoutedEventArgs e) => model.OpenLegacy();
    private async void ReadSave(object? s, RoutedEventArgs e) => await model.RunSaveToolAsync("Read");
    private async void BackupSave(object? s, RoutedEventArgs e) => await model.RunSaveToolAsync("Backup");
    private async void PrepareSave(object? s, RoutedEventArgs e) => await model.RunSaveToolAsync("Prepare");
    private async void PrepareAllSave(object? s, RoutedEventArgs e) => await model.RunSaveToolAsync("PrepareAll");
    private async void InspectPackage(object? s, RoutedEventArgs e) => await model.InspectPackageAsync();
    private async void ExtractUsbProfile(object? s, RoutedEventArgs e) => await model.ExtractUsbProfileAsync();
    private async void RefreshXbox360EditorProfile(object? s, RoutedEventArgs e) => await model.RefreshXbox360EditorProfileAsync();
    private void RefreshLocalXbox360Profiles(object? s, RoutedEventArgs e) => model.RefreshLocalXbox360Profiles();
    private async void LoadSelectedXbox360Profile(object? s, RoutedEventArgs e) => await model.LoadSelectedXbox360ProfileAsync();
    private async void LoadXbox360Profile(object? s, RoutedEventArgs e) => await model.LoadXbox360ProfileAsync();
    private async void LoadXbox360Achievements(object? s, RoutedEventArgs e) => await model.LoadXbox360AchievementsAsync();
    private void SetXbox360Achievement(object? s, RoutedEventArgs e) => model.SetXbox360Achievement();
    private async void SaveXbox360Edits(object? s, RoutedEventArgs e) => await model.SaveXbox360EditsAsync();
    private async void InjectXbox360Profile(object? s, RoutedEventArgs e) => await model.InjectXbox360ProfileAsync();
    private void OpenProfilesFolder(object? s, RoutedEventArgs e) => model.OpenProfilesFolder();
    private void RefreshHorizon(object? s, RoutedEventArgs e) => model.RefreshHorizonStatus();
    private void OpenHorizon(object? s, RoutedEventArgs e) => model.OpenHorizonFolder();
    private async void ScanGfwl(object? s, RoutedEventArgs e) => await model.ScanGfwlAsync();
    private async void InjectGfwl(object? s, RoutedEventArgs e) => await model.InjectGfwlAsync();
    private async void RefreshGfwlAchievements(object? s, RoutedEventArgs e) => await model.RefreshGfwlAchievementsAsync();
    private async void EarnGfwlAchievement(object? s, RoutedEventArgs e) => await model.EarnSelectedGfwlAchievementAsync();
    private async void EarnGfwlFromDoubleTap(object? s, TappedEventArgs e) => await model.EarnSelectedGfwlAchievementAsync();
    private async void ChoosePackage(object? s, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select Xbox 360 package", AllowMultiple = false });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) model.PackagePath = path;
    }
    private async void ShowSteam(object? s, RoutedEventArgs e) => await model.RefreshSteamAsync();
    private async void LoadSteam(object? s, RoutedEventArgs e) => await model.LoadSteamAchievementsAsync();
    private void ShowSteamAchievements(object? s, RoutedEventArgs e) => model.Navigate("SteamAchievements");
    private async void OpenSteamAchievements(object? s, RoutedEventArgs e)
    {
        if (s is Button { Tag: AchievementLabs.Models.SteamGameItem game }) { model.SelectedSteamGame = game; await model.LoadSteamAchievementsAsync(); }
    }
    private async void SaveSteam(object? s, RoutedEventArgs e) => await model.StoreSteamAchievementsAsync();
    private void InvertSteam(object? s, RoutedEventArgs e) => model.InvertSteam();
    private void RevertSteam(object? s, RoutedEventArgs e) => model.RevertSteam();
    private async void ResetSteamStats(object? s, RoutedEventArgs e) => await model.ResetSteamStatsAsync();
    private async void RefreshSteamMetadata(object? s, RoutedEventArgs e) => await model.RefreshSteamMetadataAsync();
    private void SelectAllSteam(object? s, RoutedEventArgs e) => model.SetSteamDesired(true);
    private void ClearAllSteam(object? s, RoutedEventArgs e) => model.SetSteamDesired(false);
    private async void StartSteamPresence(object? s, RoutedEventArgs e) => await model.StartSteamPresenceAsync();
    private void StopSteamPresence(object? s, RoutedEventArgs e) => model.StopSteamPresence();
    private async void ShowEpic(object? s, RoutedEventArgs e) => await model.OpenPlatformAsync("Epic");
    private async void ShowUbisoft(object? s, RoutedEventArgs e) => await model.OpenPlatformAsync("Ubisoft");
    private async void RefreshPlatform(object? s, RoutedEventArgs e) => await model.OpenPlatformAsync(model.IsEpic ? "Epic" : "Ubisoft");
    private void OpenPlatformFolder(object? s, RoutedEventArgs e) => model.OpenPlatformFolder(false);
    private void OpenPlatformCaptures(object? s, RoutedEventArgs e) => model.OpenPlatformFolder(true);
    private void CopyPlatformDetail(object? s, RoutedEventArgs e) { if (model.SelectedPlatformDetail is { } row) Workflows.NativeClipboard.SetText($"{row.Id}\n{row.Name}\n{row.Detail}\n{row.State}"); }
    private void SavePlatformConfig(object? s, RoutedEventArgs e) => model.SavePlatformConfig();
    private void InstallUbisoftSpool(object? s, RoutedEventArgs e) => model.InstallSelectedUbisoftSpool();
    private void InstallUbisoftOverlay(object? s, RoutedEventArgs e) => model.InstallUbisoftOverlay();
    private async void ChooseUbisoftSpoolFolder(object? s, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Ubisoft achievement spool folder", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) model.UbisoftSpoolTarget = path;
    }
    private async void ChooseUbisoftOverlayFolder(object? s, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Ubisoft game overlay folder", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) model.UbisoftOverlayTarget = path;
    }
    private async void CopyPlatformContext(object? s, RoutedEventArgs e) { if (model.SelectedPlatformTitle is { } title && Clipboard != null) await Clipboard.SetTextAsync(title.Context); }
    private async void StartPresence(object? s, RoutedEventArgs e) => await model.StartPresenceAsync();
    private async void LookupSpoofTitle(object? s, RoutedEventArgs e) => await model.LookupSpoofTitleAsync();
    private void StopPresence(object? s, RoutedEventArgs e) => model.StopPresence();
    private void ShowStats(object? s, RoutedEventArgs e) => model.OpenStats();
    private void UseStatsTitle(object? s, RoutedEventArgs e) => model.UseSelectedTitleForStats();
    private async void SetDiscoveredStat(object? s, RoutedEventArgs e) { if (s is Button { Tag: Workflows.StatsEditorViewModel.StatDisplayItem item }) await model.StatsEditor.UpdateSingleStat(item); }
    private async void ReadStats(object? s, RoutedEventArgs e) => await model.ReadStatsAsync();
    private async void WriteStat(object? s, RoutedEventArgs e) => await model.WriteStatAsync();
    private void ToggleApiServer(object? s, RoutedEventArgs e) => model.ToggleApiServer();
    private void OpenApiAddress(object? s, RoutedEventArgs e) => model.OpenApiAddress();
    private void RestartElevated(object? s, RoutedEventArgs e) => model.RestartElevated();
    private async void AttachManual(object? s, RoutedEventArgs e) => await model.AttachManualAsync();
    private void StartBrowserLogin(object? s, RoutedEventArgs e) => model.StartBrowserLogin();
    private async void CompleteBrowserLogin(object? s, RoutedEventArgs e) => await model.CompleteBrowserLoginAsync();
    private async void UnlockSelected(object? s, RoutedEventArgs e) => await model.UnlockSelectedAsync();
    private async void UnlockAll(object? s, RoutedEventArgs e) => await model.UnlockAllAsync();
    private async void RefreshLibrary(object? s, RoutedEventArgs e) => await model.RefreshLibraryAsync();
    private async void LookupTitle(object? s, RoutedEventArgs e) { try { await model.LookupTitleAsync(); } catch (OperationCanceledException) { } }
    private async void RefreshAchievements(object? s, RoutedEventArgs e) { try { await model.RefreshAchievementsAsync(); } catch (OperationCanceledException) { } }
    private async void ExportCsv(object? s, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export visible achievements", SuggestedFileName = "achievements.csv", DefaultExtension = "csv", FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }] });
        if (file?.TryGetLocalPath() is not { } path) return;
        try { await model.ExportCsvAsync(path); } catch { model.Notice = "Could not export achievements. Check the destination and try again."; }
    }
    private void ApplyAccent() => Application.Current!.Resources["Accent"] = new SolidColorBrush(Color.Parse(model.MintAccent ? "#8AD7A0" : "#70C98A"));
    private void ShowSettings(object? s, RoutedEventArgs e) => model.Navigate("Settings");
    private void ShowDiagnostics(object? s, RoutedEventArgs e) => model.Navigate("Diagnostics");
    private async void CheckUpdates(object? s, RoutedEventArgs e) => await model.CheckForUpdatesAsync(true);
    private void InstallUpdate(object? s, RoutedEventArgs e) { if (model.InstallUpdate()) Close(); }
    private async void SaveSettings(object? s, RoutedEventArgs e) => await model.SavePreferencesAsync();
    private async void SaveEventToken(object? s, RoutedEventArgs e) => await model.SaveEventTokenAsync();
    private void ClearEventToken(object? s, RoutedEventArgs e) => model.ClearEventToken();
    private async Task ShowEventTokenRequiredAsync()
    {
        var dialog = new Window { Title = "Event token needed", Width = 470, Height = 235, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var openSettings = new Button { Content = "Open Settings", Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel" };
        openSettings.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new Border { Padding = new Thickness(24), Child = new StackPanel { Spacing = 16, Children = { new TextBlock { Text = "An event token is needed", FontSize = 22 }, new TextBlock { Text = "Paste a current event token in Settings before unlocking an event-based achievement. The saved token is encrypted for your Windows account.", TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { openSettings, cancel } } } } };
        if (await dialog.ShowDialog<bool>(this)) model.Navigate("Settings");
    }
    private async void TestEventReplacements(object? s, RoutedEventArgs e) => await model.TestEventReplacementsAsync();
    private async void InspectCatalog(object? s, RoutedEventArgs e) => await model.InspectCatalogAsync();
    private async void Disconnect(object? s, RoutedEventArgs e) => await model.DisconnectAsync();
    private async void ForgetSavedSession(object? s, RoutedEventArgs e) => await model.ForgetSavedSessionAsync();
    private async void ChooseEvents(object? s, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Events folder", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) model.EventsDirectory = path;
    }
    private async void ChooseSession(object? s, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select saved Xbox session", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) model.SessionPath = path;
    }
    private void ToggleAccent(object? s, RoutedEventArgs e)
    {
        model.MintAccent = !model.MintAccent;
    }
}
