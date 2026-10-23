using Avalonia.Controls.Notifications;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
namespace AchievementLabs.Desktop;
public partial class MainWindow : Window
{
    private readonly DesktopModel model = new();
    private double normalWidth = 1440, normalHeight = 920;
    private PixelPoint? normalPosition;
    private bool windowPreferencesLoaded;
    private static readonly string windowSizePath = AchievementLabs.Core.AchievementLabsPaths.LocalFile("window-size.json");
    private readonly DispatcherTimer dlcCatalogueTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly DispatcherTimer xboxPresenceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer tokenMaintenanceTimer = new() { Interval = AchievementLabs.Core.XboxTokenMaintenancePolicy.CheckInterval };

    public MainWindow()
    {
        InitializeComponent(); DataContext = model; model.EventTokenRequired = ShowEventTokenRequiredAsync;
        var notifications = new WindowNotificationManager(this) { Position = NotificationPosition.TopRight, MaxItems = 3 };
        model.AutoUnlockFailure = message => { notifications.Show(new Notification("Unlock failed", message, NotificationType.Error, TimeSpan.FromSeconds(10))); model.NotifyWindows("Unlock failed", message); };
        model.AutoUnlockCompleted = message => notifications.Show(new Notification("Auto unlock complete", message, NotificationType.Success, TimeSpan.FromSeconds(10)));
        model.MirrorAlert = (title, message) => notifications.Show(new Notification(title, message, NotificationType.Error, TimeSpan.FromSeconds(15)));
        model.MirrorPreflightReview = ReviewMirrorPreflightAsync;
        model.SpooferFailure = message => notifications.Show(new Notification("Spoofer stopped", message, NotificationType.Error, TimeSpan.FromSeconds(10)));
        model.WindowsNotification = (title, message) =>
        {
            if (AppContext.TryGetSwitch("AchievementLabs.OfflineChecks", out var checks) && checks) return;
            if (!WindowsNotifications.TryShow(title, message)) model.Notice = "Windows notification could not be sent. Check Windows notification settings for Achievement Labs.";
        };
        AchievementLabs.MultiSelect.BatchPicker.Attach(this);
        Presentation.GameSessionWorkspace.Attach(this);
        AchievementLabs.MultiSelect.SearchClearButtons.Attach(this);
        Workflows.NativeClipboard.WriteAsync = async text => { try { if (Clipboard != null) await Clipboard.SetTextAsync(text); } catch { model.Notice = "Could not copy to the clipboard."; } };
        xboxPresenceTimer.Tick += (_, _) => model.RefreshXboxPcAppPresence();
        var offlineChecks = AppContext.TryGetSwitch("AchievementLabs.OfflineChecks", out var offline) && offline;
        var startupWindowCheck = Environment.GetCommandLineArgs().Contains("--window-startup-test");
        if (!offlineChecks && !Environment.GetCommandLineArgs().Contains("--smoke-test") && !Environment.GetCommandLineArgs().Contains("--release-smoke-test"))
        {
            var maximize = true;
            try
            {
                var path = AchievementLabs.Core.AchievementLabsPaths.LocalFile("native-settings.json");
                if (!startupWindowCheck && File.Exists(path)) maximize = System.Text.Json.JsonSerializer.Deserialize<AchievementLabs.Core.DesktopPreferences>(File.ReadAllText(path))?.AutoMaximize ?? true;
            }
            catch { /* A missing/corrupt settings file must not prevent opening the window. */ }
            WindowState = maximize ? WindowState.Maximized : WindowState.Normal;
        }
        dlcCatalogueTimer.Tick += async (_, _) => { await model.RefreshDlcCatalogueAsync(false); await model.RefreshEventCatalogueAsync(false); };
        tokenMaintenanceTimer.Tick += async (_, _) => await model.MaintainTokensAsync();
        if (!offlineChecks && !Environment.GetCommandLineArgs().Contains("--smoke-test")) { xboxPresenceTimer.Start(); dlcCatalogueTimer.Start(); tokenMaintenanceTimer.Start(); }
        SizeChanged += (_, _) => {
            if (windowPreferencesLoaded && WindowState == WindowState.Normal) { normalWidth = Width; normalHeight = Height; }
        };
        PositionChanged += (_, _) => { if (windowPreferencesLoaded && WindowState == WindowState.Normal) normalPosition = Position; };
        Closing += (_, _) => {
            if (!windowPreferencesLoaded || !model.RememberWindowSize) return;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(windowSizePath)!);
                if (WindowState == WindowState.Normal) { normalWidth = Width; normalHeight = Height; normalPosition = Position; }
                File.WriteAllText(windowSizePath, System.Text.Json.JsonSerializer.Serialize(new AchievementLabs.Core.WindowPlacement(normalWidth, normalHeight, normalPosition?.X, normalPosition?.Y)));
            } catch { /* Closing must still succeed if the settings folder is unavailable. */ }
        };
        Closed += (_, _) => { tokenMaintenanceTimer.Stop(); xboxPresenceTimer.Stop(); dlcCatalogueTimer.Stop(); model.Dispose(); };
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.MintAccent)) ApplyAccent();
            if (e.PropertyName == nameof(model.LibrarySort) ||
                e.PropertyName == nameof(model.Games) && model.LibrarySort == "Last Played" && !model.TotalsRunning)
                Dispatcher.UIThread.Post(() =>
                {
                    var list = this.FindControl<ListBox>("XboxLibraryList");
                    var scroll = list?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                    if (scroll != null) scroll.Offset = new Vector(0, 0);
                }, DispatcherPriority.Background);
        };
        Opened += async (_, _) =>
        {
            // Headless regression tests must never attach to a real account.
            if (offlineChecks) return;
            if (startupWindowCheck)
            {
                var initial = WindowState;
                model.AutoMaximize = true; RestoreWindowPreferences();
                await Task.Delay(500);
                var checkArgs = Environment.GetCommandLineArgs();
                var output = checkArgs.SkipWhile(a => a != "--output").Skip(1).First();
                Directory.CreateDirectory(output);
                var passed = initial == WindowState.Maximized && WindowState == WindowState.Maximized;
                File.WriteAllText(Path.Combine(output,"window-startup.txt"), $"{(passed ? "PASS" : "FAIL")}: initial={initial}; after placement restore={WindowState}; maximized startup on Windows. No account calls.");
                if (!passed) Environment.ExitCode = 1;
                Close(); return;
            }
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
                await model.LoadPreferencesAsync(); RestoreWindowPreferences(); _ = model.CheckForUpdatesAsync(); _ = model.RefreshDlcCatalogueAsync(false); _ = model.RefreshEventCatalogueAsync(false); await model.AttachXboxPcAppAsync(); return;
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
                foreach (var page in new[] { "SteamLibrary", "SteamAchievements", "SteamQueue", "Epic", "Ubisoft", "Stats", "Gfwl", "Queues", "Tools" })
                {
                    model.Navigate(page); await Task.Delay(100); bitmap.Render(root); bitmap.Save(Path.Combine(output, "native-" + page.ToLowerInvariant() + ".png"), new PngBitmapEncoderOptions());
                }
                model.OpenQueues(); BuildQueueTab.IsSelected = true; await Task.Delay(100);
                bitmap.Render(root); bitmap.Save(Path.Combine(output, "native-build-queue.png"), new PngBitmapEncoderOptions());
                Width = 1120; Height = 720; model.Navigate("Settings"); await Task.Delay(150);
                using (var compact = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height), new Vector(96,96))) { compact.Render(root); compact.Save(Path.Combine(output, "native-compact.png"), new PngBitmapEncoderOptions()); }
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "PresentationFramework" or "WPF-UI")) throw new Exception("WPF UI assembly loaded into native desktop.");
                File.WriteAllText(Path.Combine(output, "smoke-test.txt"), "PASS: real 43-row import and synthetic 10,000-row import/filter/search; native rendering; preferences and protected sessions; catalog ordering/missing templates; known and unknown progress mapping; settings/catalog navigation. No account calls.");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            Close();
        };
    }
    private void RestoreWindowPreferences()
    {
        var saved = new AchievementLabs.Core.WindowPlacement(1440, 920);
        if (model.RememberWindowSize && File.Exists(windowSizePath)) {
            try { saved = System.Text.Json.JsonSerializer.Deserialize<AchievementLabs.Core.WindowPlacement>(File.ReadAllText(windowSizePath)) ?? saved; }
            catch { model.Notice = "Saved window placement could not be read; using the default."; }
        }
        WindowState = WindowState.Normal;
        var screen = saved.X is { } x && saved.Y is { } y
            ? Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(new PixelPoint(x, y))) ?? Screens.Primary
            : Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null) {
            var area = screen.WorkingArea;
            var frame = FrameSize;
            var frameWidth = frame.HasValue ? Math.Max(0, frame.Value.Width - ClientSize.Width) + 2 : 16;
            var frameHeight = frame.HasValue ? Math.Max(0, frame.Value.Height - ClientSize.Height) + 2 : 48;
            saved = AchievementLabs.Core.WindowPlacement.Fit(saved, area.X, area.Y, area.Width, area.Height, screen.Scaling, frameWidth, frameHeight);
            // Very small/high-DPI displays may have less usable space than the usual UI minimum.
            MinWidth = Math.Min(1120, saved.Width); MinHeight = Math.Min(720, saved.Height);
        }
        Width = saved.Width; Height = saved.Height;
        if (saved.X is { } left && saved.Y is { } top) Position = new PixelPoint(left, top);
        normalWidth = Width; normalHeight = Height; normalPosition = Position;
        windowPreferencesLoaded = true;
        if (model.AutoMaximize)
        {
            WindowState = WindowState.Maximized;
            Dispatcher.UIThread.Post(() => { if (model.AutoMaximize) WindowState = WindowState.Maximized; }, DispatcherPriority.Loaded);
        }
    }
    private async void SubmitTextField(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || sender is not TextBox field || field.AcceptsReturn) return;
        // Explicit allowlist: Enter builds queues for review but never starts unlock/presence.
        var action = field.Tag as string;
        try {
        if (action == "open-title" && model.CanQuery) { e.Handled = true; await model.LookupTitleAsync(); }
        else if (action == "resolve-store-title" && model.CanInteract) { e.Handled = true; await model.ResolveStoreTitleAsync(); }
        else if (action == "search-titles" && model.CanInteract) { e.Handled = true; await model.SearchTitlesAsync(); }
        else if (action == "search-profile" && model.CanQuery) { e.Handled = true; await model.SearchGamertagAsync(); }
        else if (action == "lookup-spoof" && model.CanLookupSpoofTitle) { e.Handled = true; await model.LookupSpoofTitleAsync(); }
        else if (action == "load-steam" && model.CanInteract) { e.Handled = true; await model.LoadSteamAchievementsAsync(); }
        else if (action == "build-xbox-queue" && model.CanQuery && model.XboxQueue.IsConfigEnabled
            && !string.IsNullOrWhiteSpace(model.XboxQueue.ReferenceGamertag) && !string.IsNullOrWhiteSpace(model.XboxQueue.TitleId)
            && model.XboxQueue.BuildQueueCommand.CanExecute(null)) { e.Handled = true; await model.XboxQueue.BuildQueueCommand.ExecuteAsync(null); }
        else if (action == "save-token") { e.Handled = true; await model.SaveEventTokenAsync(); }
        else if (action == "add-build-game" && model.XboxBuildQueue.CanAdd
            && !string.IsNullOrWhiteSpace(model.XboxBuildQueue.ReferenceGamertag) && !string.IsNullOrWhiteSpace(model.XboxBuildQueue.TitleId)
            && model.XboxBuildQueue.AddGameCommand.CanExecute(null)) { e.Handled = true; await model.XboxBuildQueue.AddGameCommand.ExecuteAsync(null); }
        } catch (OperationCanceledException) { }
        catch (Exception) { model.Notice = "The action could not be completed. Check the field and connection, then try again."; }
    }
    private async void RefreshEventCatalogue(object? sender, RoutedEventArgs e) => await model.RefreshEventCatalogueAsync();
    private async void ImportEventPack(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import event pack JSON", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Event pack JSON") { Patterns = ["*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await model.ImportEventPackAsync(path);
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
    private async void ResolveStoreTitle(object? s, RoutedEventArgs e) => await model.ResolveStoreTitleAsync();
    private async void CopyStoreTitleId(object? s, RoutedEventArgs e)
    {
        if (model.HasStoreTitleId && Clipboard != null) await Clipboard.SetTextAsync(model.StoreTitleId);
    }
    private async void OpenStoreAchievements(object? s, RoutedEventArgs e)
    {
        if (!model.HasStoreTitleId) return;
        if (!model.CanQuery) { model.Notice = "Connect Xbox before opening achievements. You can still copy the Title ID."; return; }
        var game = model.Games.FirstOrDefault(g => g.Id == model.StoreTitleId)
            ?? new Game(model.StoreTitleId, model.StoreTitleName, DesktopModel.IsKnownWindows8Title(model.StoreTitleId) ? "Windows8" : "XboxOne", 0, 0, 0);
        if (await model.IsGfwlTitleAsync(game)) { model.OpenGfwlTitle(game); return; }
        await model.SelectGameAsync(game);
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
    private void OpenGameTitleSpoof(object? sender, RoutedEventArgs e) => model.OpenGameSpoofer();
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
    private void ShowSteamQueue(object? s, RoutedEventArgs e) => model.Navigate("SteamQueue");
    private void ShowMirror(object? s, RoutedEventArgs e) => model.OpenMirror();
    private async Task<bool> ReviewMirrorPreflightAsync(IReadOnlyList<Workflows.MirrorPreflightIssue> issues)
    {
        var blocking = issues.Any(i => i.Blocking);
        var dialog = new Window { Title = "Achievement Mirror check", Width = 560, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var openMirror = new Button { Content = "Open Achievement Mirror" };
        var start = new Button { Content = "Start without mirroring these", IsVisible = !blocking };
        var cancel = new Button { Content = "Cancel" };
        var openRequested = false;
        openMirror.Click += (_, _) => { openRequested = true; dialog.Close(false); };
        start.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        var list = new StackPanel { Spacing = 8 };
        foreach (var issue in issues)
            list.Children.Add(new TextBlock { Text = (issue.Blocking ? "• " : "◦ ") + issue.TitleName + ": " + issue.Message, TextWrapping = TextWrapping.Wrap });
        dialog.Content = new Border { Padding = new Thickness(24), Child = new StackPanel { Spacing = 16, Children = {
            new TextBlock { Text = blocking ? "Fix these before Auto Unlock starts" : "Some games won't be mirrored", FontSize = 20, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = blocking ? "Achievement Mirror is copying your Xbox unlocks to Steam, but it can't mirror everything in this queue. Nothing has been unlocked." : "These games are in the queue but aren't being mirrored to Steam.", TextWrapping = TextWrapping.Wrap },
            list,
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { openMirror, start, cancel } }
        } } };
        var proceed = await dialog.ShowDialog<bool>(this);
        if (openRequested) model.OpenMirror();
        return proceed;
    }
    private void OpenLibraryStats(object? s, RoutedEventArgs e) { if (s is Button { Tag: Game game }) model.OpenLibraryStats(game); }
    private void ShowQueues(object? s, RoutedEventArgs e) => model.OpenQueues();
    private async void RunXboxQueue(object? s, RoutedEventArgs e) => await model.RunXboxQueueAsync();
    private async void RunXboxBuildQueue(object? s, RoutedEventArgs e) => await model.RunXboxBuildQueueAsync();
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
    private async void SetDiscoveredStat(object? s, RoutedEventArgs e) { if (model.CanQuery && s is Button { Tag: Workflows.StatsEditorViewModel.StatDisplayItem item }) await model.StatsEditor.UpdateSingleStat(item); }
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
    private async void ExportVerifiedTotals(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export all cached successful totals", SuggestedFileName = "achievement-verified-totals.csv", DefaultExtension = "csv" });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try { await model.ExportVerifiedTotalsAsync(path); }
        catch { model.Notice = "Could not export verified totals."; }
    }
    private async void FillMissingTotals(object? sender, RoutedEventArgs e) => await model.FillMissingTotalsAsync();
    private void StopTotalsScan(object? sender, RoutedEventArgs e) => model.CancelFillTotals();
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
    private async void RefreshDlcCatalogue(object? s, RoutedEventArgs e) => await model.RefreshDlcCatalogueAsync();
    private async void ImportCompletionExports(object? s, RoutedEventArgs e) => await model.ImportCompletionExportsAsync();
    private async void ScanCompletionProgress(object? s, RoutedEventArgs e) => await model.ScanMissingCompletionAsync();
    private void StopCompletionScan(object? s, RoutedEventArgs e) => model.StopCompletionScan();
    private void ResetCompletionColours(object? s, RoutedEventArgs e) => model.ResetCompletionColours();
    private void OpenDebugReports(object? s, RoutedEventArgs e) => model.OpenDebugReports();
    private void OpenCatalogueReview(object? s, RoutedEventArgs e) => model.OpenCatalogueReview((s as Button)?.Tag as string ?? "corrections");
    private async void OpenBulkAchievementExport(object? s, RoutedEventArgs e)
    {
        if (!model.CanQuery) return;
        try { await AchievementLabs.MultiSelect.ExportAllView.Show(this, model); }
        catch { model.Notice = "Could not open the bulk achievement export."; }
    }
    private void ShowSettings(object? s, RoutedEventArgs e) => model.Navigate("Settings");
    private void ShowDiagnostics(object? s, RoutedEventArgs e) => model.Navigate("Diagnostics");
    private void TestWindowsNotification(object? s, RoutedEventArgs e) => model.TestWindowsNotification();
    private async void ShowMobileSync(object? sender, RoutedEventArgs args)
    {
        if (!model.CanQuery) return;
        try {
            var frames = AchievementLabs.Core.MobileTransfer.QrFrames(model.CreateMobileTransfer());
            var eventOnly=false; var signInOnly=false;
            var dialog = new Window { Title="Mobile sync · iPhone / iPad / Android",Width=570,Height=800,MinWidth=500,MinHeight=600,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            var image = new Avalonia.Controls.Image { Width=410,Height=410,Stretch=Avalonia.Media.Stretch.Uniform };
            var status = new TextBlock { TextWrapping=TextWrapping.Wrap,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center };
            var close = new Button { Content="Close" }; close.Click += (_,_)=>dialog.Close();
            var previous = new Button { Content="Previous" }; var next = new Button { Content="Next" };
            var automatic = new CheckBox { Content="Cycle automatically",IsChecked=true };
            var index=0; var expires=DateTimeOffset.UtcNow.AddMinutes(10);
            Avalonia.Media.Imaging.Bitmap? bitmap=null;
            void ShowFrame() {
                using var generator = new QRCoder.QRCodeGenerator();
                using var data = generator.CreateQrCode(frames[index],QRCoder.QRCodeGenerator.ECCLevel.M);
                using var qr = new QRCoder.PngByteQRCode(data);
                using var stream = new MemoryStream(qr.GetGraphic(5));
                var old=bitmap; bitmap=new Avalonia.Media.Imaging.Bitmap(stream); image.Source=bitmap; old?.Dispose();
                status.Text=$"{(eventOnly ? "Event token only" : signInOnly ? "Sign-in only" : "Sign-in + event token")} · code {index+1} of {frames.Length} · expires {expires.LocalDateTime:t}";
            }
            var timer=new Avalonia.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(0.5) };
            var accountMode = new RadioButton { Content="Sign-in + event token",GroupName="MobileTransferMode",IsChecked=true };
            var signInMode = new RadioButton { Content="Sign-in only",GroupName="MobileTransferMode" };
            var eventMode = new RadioButton { Content="Event token only",GroupName="MobileTransferMode" };
            var modeHeading = new TextBlock { Text="SIGN-IN + EVENT TOKEN",FontSize=20,FontWeight=Avalonia.Media.FontWeight.Bold,Foreground=Avalonia.Media.Brushes.LimeGreen };
            var speed = new ComboBox { ItemsSource=new[]{"Fast · 0.5 seconds","Balanced · 0.8 seconds","Slow · 1.2 seconds"},SelectedIndex=0,MinWidth=220 };
            speed.SelectionChanged += (_,_)=> { timer.Interval=TimeSpan.FromSeconds(speed.SelectedIndex==0 ? 0.5 : speed.SelectedIndex==1 ? 0.8 : 1.2); };
            void ChangeMode(bool only, bool signOnly=false) {
                modeHeading.Text=only ? "EVENT TOKEN ONLY" : signOnly ? "SIGN-IN ONLY" : "SIGN-IN + EVENT TOKEN";
                try { var replacement=AchievementLabs.Core.MobileTransfer.QrFrames(model.CreateMobileTransfer(only,signOnly)); Array.Clear(frames); frames=replacement; eventOnly=only; signInOnly=signOnly; index=0; expires=DateTimeOffset.UtcNow.AddMinutes(10); previous.IsEnabled=true; next.IsEnabled=true; ShowFrame(); timer.Start(); }
                catch { timer.Stop(); image.Source=null; previous.IsEnabled=false; next.IsEnabled=false; status.Text="Save an event token in PC Settings before creating an event-token QR."; }
            }
            signInMode.Click += (_,_)=>ChangeMode(false,true); accountMode.Click += (_,_)=>ChangeMode(false); eventMode.Click += (_,_)=>ChangeMode(true);
            previous.Click += (_,_)=>{ index=(index+frames.Length-1)%frames.Length; ShowFrame(); };
            next.Click += (_,_)=>{ index=(index+1)%frames.Length; ShowFrame(); };
            timer.Tick += (_,_)=>{ if(DateTimeOffset.UtcNow>=expires) { image.Source=null; status.Text="Expired. Close and generate a new code."; timer.Stop(); previous.IsEnabled=false; next.IsEnabled=false; }
                else if(automatic.IsChecked==true) { index=(index+1)%frames.Length; ShowFrame(); } };
            dialog.Content = new ScrollViewer { Content = new StackPanel { Margin=new Thickness(20),Spacing=12,Children={
                new TextBlock { Text="On iPhone / iPad, open Home → Sync from PC → Scan PC QR codes. Keep the camera pointed here until every code is collected.",TextWrapping=TextWrapping.Wrap },
                new StackPanel { Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=18,Children={accountMode,signInMode,eventMode} },
                modeHeading,
                new StackPanel { Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=10,Children={new TextBlock { Text="QR speed",VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center },speed} },
                image,status,new StackPanel { Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=12,Children={previous,next,automatic} },
                new TextBlock { Text="Private sign-in codes: do not share screenshots. Sign-in-only mode excludes the event token. Event-token mode excludes account authorization. Microsoft renewal credentials always stay on PC.",TextWrapping=TextWrapping.Wrap },close } } };
            ShowFrame(); timer.Start();
            try { await dialog.ShowDialog(this); } finally { timer.Stop(); image.Source=null; bitmap?.Dispose(); Array.Clear(frames); }
        } catch { model.Notice="Could not create mobile QR transfer. Connect an Xbox account first."; }
    }
    private async void ExportMobileTransfer(object? sender, RoutedEventArgs args)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title="Export encrypted mobile sign-in",SuggestedFileName="AchievementLabs-PC.labslink",DefaultExtension="labslink" });
        if (file?.TryGetLocalPath() is not { } path) return;
        try {
            var transfer = model.CreateMobileTransfer();
            await File.WriteAllBytesAsync(path,transfer.Bytes);
            var dialog = new Window { Title="Mobile sync · iPhone / iPad / Android",Width=580,Height=340,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            var copy = new Button { Content="Copy pairing key" }; var close = new Button { Content="Close" };
            copy.Click += async (_,_) => { if (Clipboard != null) await Clipboard.SetTextAsync(transfer.PairingKey); };
            close.Click += (_,_) => dialog.Close();
            dialog.Content = new StackPanel { Margin=new Thickness(24),Spacing=14,Children={
                new TextBlock { Text="Encrypted file saved. Import it in the mobile app's Settings → PC sync, then enter this key. The file expires in 10 minutes.",TextWrapping=Avalonia.Media.TextWrapping.Wrap },
                new SelectableTextBlock { Text=transfer.PairingKey,TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=17 },
                new TextBlock { Text="Keep the file and key private. This exports the current authorization and event token, not Microsoft renewal credentials.",TextWrapping=Avalonia.Media.TextWrapping.Wrap },copy,close } };
            await dialog.ShowDialog(this); model.Notice="Encrypted mobile transfer exported.";
        } catch { model.Notice="Could not export mobile sign-in. Connect Xbox and check the destination."; }
    }
    private async void DeleteSavedGameQueue(object? sender, RoutedEventArgs e)
    {
        if (model.QueueActive || model.XboxQueue.IsRunning || model.XboxBuildQueue.IsAdding) { model.Notice = "Pause Auto Unlock before deleting saved queues."; return; }
        var saved = model.XboxQueue.SelectedSavedGameQueue;
        if (saved == null) { model.Notice = "Choose a saved game queue first."; return; }
        var owner = model.QueueOwnerXuid;
        var dialog = new Window { Title = "Delete saved queue?", Width = 460, Height = 240, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var remove = new Button { Content = "Delete saved queue" };
        var cancel = new Button { Content = "Cancel" };
        remove.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new Border { Padding = new Thickness(24), Child = new StackPanel { Spacing = 16, Children = {
            new TextBlock { Text = "Delete " + saved.Name + "?", FontSize = 20, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "This also removes its loaded Auto Unlock queue. Other games and Xbox achievements are unchanged.", TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { cancel, remove } }
        } } };
        if (await dialog.ShowDialog<bool>(this)) await model.XboxQueue.DeleteSavedGameQueueAsync(saved.TitleId, owner);
    }
    private async void CheckUpdates(object? s, RoutedEventArgs e) => await model.CheckForUpdatesAsync(true);
    private void InstallUpdate(object? s, RoutedEventArgs e) { if (model.InstallUpdate()) Close(); }
    private async void SaveSettings(object? s, RoutedEventArgs e) => await model.SavePreferencesAsync();
    private async void AcquireAccountEventToken(object? sender, RoutedEventArgs e) => await model.AcquireAccountEventTokenAsync(this);
    private async void SaveEventToken(object? s, RoutedEventArgs e) => await model.SaveEventTokenAsync();
    private async void ClearEventToken(object? s, RoutedEventArgs e) => await model.ClearEventTokenAsync();
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
    private async void ExportQueueSync(object? sender,RoutedEventArgs e)
    {
        try {
            var json=model.XboxQueue.ExportQueueSyncFile();
            var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title="Export queue sync file",SuggestedFileName="AchievementLabs-Queues.json",DefaultExtension="json",FileTypeChoices=[new FilePickerFileType("JSON") { Patterns=["*.json"] }] });
            if(file==null)return;await using var stream=await file.OpenWriteAsync();stream.SetLength(0);await using var writer=new System.IO.StreamWriter(stream);await writer.WriteAsync(json);model.Notice="Queue sync file exported. Contains game progress and delays, with no sign-in credentials.";
        } catch(Exception error) { model.Notice=error.Message; }
    }
    private async void ImportQueueSync(object? sender,RoutedEventArgs e)
    {
        try {
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title="Import queue sync file",AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("JSON") { Patterns=["*.json"] }] });
            if(files.FirstOrDefault() is not { } file)return;await using var stream=await file.OpenReadAsync();
            using var bytes=new System.IO.MemoryStream();var buffer=new byte[8192];int count;
            while((count=await stream.ReadAsync(buffer))>0) { if(bytes.Length+count>8*1024*1024)throw new System.IO.InvalidDataException("Queue sync file exceeds 8 MB.");await bytes.WriteAsync(buffer.AsMemory(0,count)); }
            await model.XboxQueue.ImportQueueSyncFileAsync(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));model.Notice=model.XboxQueue.StatusText;
        } catch(Exception error) { model.Notice=error.Message; }
    }
    private void ToggleAccent(object? s, RoutedEventArgs e)
    {
        model.MintAccent = !model.MintAccent;
    }
}
