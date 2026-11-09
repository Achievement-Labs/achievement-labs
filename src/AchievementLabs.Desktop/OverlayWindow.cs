using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AchievementLabs.Mirror;
using AchievementLabs.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AchievementLabs.Desktop;

// In-game overlay (Alt+U): a borderless topmost window over the game's monitor with a
// slimmed-down Achievement Labs laid out as a 2x2 grid of tiles. It detects the running Steam or Xbox game, takes focus
// while open so menus and search work, and hands focus straight back to the game on close.
// Exclusive-fullscreen games cannot show any window on top, so those get a hint instead.
internal sealed class OverlayWindow : Window
{
    private static readonly IBrush Text = Brushes.White;
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#B8C0CC"));
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#7F8A99"));

    private readonly DesktopModel model;
    // Built from the main window's appearance settings (glass colour, OLED). The overlay has no blur
    // behind it, so surfaces stay mostly opaque regardless of the glass transparency setting.
    private readonly Presentation.AppearanceController appearance;
    private readonly SolidColorBrush panel = new(), card = new(), input = new(), line = new();
    private IBrush Panel => panel;
    private IBrush Card => card;
    private readonly SolidColorBrush cardDone = new();
    private readonly SolidColorBrush accent = new();
    private readonly Action<bool> visibilityChanged;
    private readonly TextBlock header = new() { FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Text, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock subheader = new() { FontSize = 13, Foreground = Muted };
    private readonly TextBlock status = new() { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl gameBody = new(), libraryBody = new(), mirrorBody = new(), presenceBody = new();
    private readonly TextBox achievementSearch = new() { PlaceholderText = "Search achievements" };
    private readonly TextBox librarySearch = new() { PlaceholderText = "Search your library by name or ID" };
    private readonly ComboBox libraryPlatform = new() { ItemsSource = new[] { "All", "Xbox", "Steam" }, SelectedIndex = 0, MinWidth = 110 };
    private readonly TextBox xboxPresenceId = new() { PlaceholderText = "Xbox title ID", Width = 220 };
    private readonly TextBox steamPresenceId = new() { PlaceholderText = "Steam app ID", Width = 220 };
    private readonly HashSet<string> selected = [];
    private readonly DispatcherTimer refreshDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private OverlayGame? game;
    private IntPtr previousForeground;
    private bool loading;

    public OverlayWindow(DesktopModel model, Presentation.AppearanceController appearance, Action<bool> visibilityChanged)
    {
        this.model = model; this.appearance = appearance; this.visibilityChanged = visibilityChanged;
        Title = "Achievement Labs overlay";
        WindowDecorations = WindowDecorations.None;
        Topmost = true; ShowInTaskbar = false; CanResize = false;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ApplyTheme();
        appearance.Changed += () => Dispatcher.UIThread.Post(() => { ApplyTheme(); if (IsVisible) Render(); });

        var detect = Button("Detect game", async () => await DetectAsync(true));
        var close = Button("Close", HideOverlay);
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,*"), ColumnDefinitions = new ColumnDefinitions("*,*") };
        AddTile(grid, 0, 0, "Current game", gameBody, achievementSearch);
        libraryPlatform.Margin = new Thickness(0, 0, 8, 0);
        AddTile(grid, 0, 1, "Library search", libraryBody, new DockPanel { Children = { Docked(libraryPlatform, Dock.Left), librarySearch } });
        AddTile(grid, 1, 0, "Achievement Mirror", mirrorBody);
        AddTile(grid, 1, 1, "Presence", presenceBody, new StackPanel { Spacing = 8, Children = {
            LabeledInput("Xbox title spoofer", xboxPresenceId), LabeledInput("Steam presence", steamPresenceId) } });
        Content = new DockPanel { Margin = new Thickness(24), Children = {
            Docked(new Border { Background = Panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 14), Margin = new Thickness(8), Child = new DockPanel { Children = {
                Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { detect, close } }, Dock.Right),
                new StackPanel { Spacing = 2, Children = { header, subheader, status,
                    new TextBlock { Text = "Alt+U or Esc to close", FontSize = 11, Foreground = Faint } } } } } }, Dock.Top),
            grid } };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { HideOverlay(); e.Handled = true; } };
        PointerPressed += (_, e) => { if (e.Source == this) HideOverlay(); };
        achievementSearch.TextChanged += (_, _) => Render();
        librarySearch.TextChanged += (_, _) => Render();
        libraryPlatform.SelectionChanged += (_, _) => Render();
        xboxPresenceId.TextChanged += (_, _) => Render();
        steamPresenceId.TextChanged += (_, _) => Render();
        refreshDebounce.Tick += (_, _) => { refreshDebounce.Stop(); if (IsVisible) Render(); };
        model.PropertyChanged += ModelChanged;
        model.Mirror.PropertyChanged += ModelChanged;
    }

    public void Toggle() { if (IsVisible) HideOverlay(); else ShowOverlay(); }

    private void ShowOverlay()
    {
        previousForeground = OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;
        if (ExclusiveFullscreenRunning())
        {
            OverlayLog.Write("Exclusive fullscreen detected; showing hint instead");
            model.NotifyWindows("Overlay unavailable in exclusive fullscreen", "Switch the game to Borderless or Windowed fullscreen to use the Alt+U overlay.");
            return;
        }
        PlaceOverForeground();
        Render();
        Show(); Activate();
        visibilityChanged(true);
        OverlayLog.Write($"Shown over {ForegroundProcessName() ?? "(none)"} at {Position} {Width}x{Height}");
        _ = DetectAsync(false);
    }

    public void HideOverlay()
    {
        if (!IsVisible) return;
        Hide();
        visibilityChanged(false);
        if (previousForeground != IntPtr.Zero) SetForegroundWindow(previousForeground);
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        Dispatcher.UIThread.Post(() => { refreshDebounce.Stop(); refreshDebounce.Start(); });
    }

    private async Task DetectAsync(bool manual)
    {
        var detected = await model.DetectRunningGameAsync();
        OverlayLog.Write($"Detected {(detected == null ? "nothing" : $"{detected.Platform} {detected.Id} {detected.Name}")}");
        if (detected == null)
        {
            if (game == null || manual) { model.Notice = "No running Steam or Xbox game detected. Search your library instead."; }
            Render();
            return;
        }
        if (game != null && game.Platform == detected.Platform && game.Id == detected.Id && !manual) { Render(); return; }
        await SetGameAsync(detected);
    }

    private async Task SetGameAsync(OverlayGame next)
    {
        if (game == null || game.Platform != next.Platform || game.Id != next.Id) selected.Clear();
        game = next; achievementSearch.Text = "";
        (next.Platform == OverlayPlatform.Steam ? steamPresenceId : xboxPresenceId).Text = next.Id;
        SelectMirrorPair();
        Render();
        if (Loaded(next)) return;
        loading = true; Render();
        try { await model.LoadOverlayGameAsync(next); }
        catch (Exception error) { model.Notice = "Could not load achievements: " + error.Message; }
        finally { loading = false; Render(); }
    }

    private bool Loaded(OverlayGame g) => g.Platform == OverlayPlatform.Steam ? model.OverlaySteamGameLoaded(g) : model.OverlayXboxGameLoaded(g);

    private void SelectMirrorPair()
    {
        if (game == null || model.Mirror.IsRunning) return;
        var pair = model.Mirror.Pairs.FirstOrDefault(p => game.Platform == OverlayPlatform.Steam ? p.SteamAppId.ToString() == game.Id : p.XboxTitleId == game.Id);
        if (pair != null) model.Mirror.SelectedPair = pair;
    }

    private void Render()
    {
        header.Text = game?.Name ?? "Achievement Labs";
        subheader.Text = game == null ? "No game selected" : $"{(game.Platform == OverlayPlatform.Steam ? "Steam app" : "Xbox title")} {game.Id}{(loading ? " · loading…" : "")}";
        gameBody.Content = GameTab();
        libraryBody.Content = LibraryTab();
        mirrorBody.Content = MirrorTab();
        presenceBody.Content = PresenceTab();
        var parts = new[] { model.Notice, game?.Platform == OverlayPlatform.Steam ? model.SteamStatus : model.ActionSummary }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct();
        status.Text = string.Join("\n", parts);
    }

    // ---- Current game ----

    private Control GameTab()
    {
        if (game == null) return Hint("No game detected. Pick one in Library search, or press Detect game while it's running.");
        if (game.Platform == OverlayPlatform.Xbox && !model.OverlayXboxConnected) return Hint("Attach the Xbox PC app in Achievement Labs to load Xbox achievements.");
        if (loading || !Loaded(game)) return Hint(loading ? "Loading achievements…" : "Achievements aren't loaded.", Button("Load achievements", async () => await SetGameAsync(game)));
        return game.Platform == OverlayPlatform.Steam ? SteamList() : XboxList();
    }

    private Control XboxList()
    {
        var rows = model.OverlayAchievements;
        var filter = achievementSearch.Text ?? "";
        var shown = rows.Where(a => filter.Length == 0 || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Unlocked).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var eligible = rows.Where(model.CanOverlayUnlockXbox).ToArray();
        selected.RemoveWhere(id => !eligible.Any(a => a.Id == id));
        var list = new StackPanel { Spacing = 6 };
        foreach (var a in shown.Take(300))
            list.Children.Add(Row(a.Id, a.Name + (a.ScoreKnown ? $"  ·  {a.Score}G" : ""), a.Description, a.Unlocked, model.CanOverlayUnlockXbox(a)));
        return ListLayout($"{rows.Count(a => a.Unlocked)} / {rows.Length} unlocked", list,
            Button($"Unlock selected ({selected.Count})", async () => { var pick = eligible.Where(a => selected.Contains(a.Id)).ToArray(); selected.Clear(); await model.UnlockOverlayXboxAsync(pick); }, selected.Count > 0 && model.CanInteract),
            Button("Unlock all eligible", async () => { selected.Clear(); await model.UnlockOverlayXboxAsync(eligible.Where(a => !a.Unlocked)); }, model.CanInteract && eligible.Any(a => !a.Unlocked)));
    }

    private Control SteamList()
    {
        var rows = model.SteamAchievements;
        var filter = achievementSearch.Text ?? "";
        var shown = rows.Where(a => filter.Length == 0 || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.IsUnlocked).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        selected.RemoveWhere(id => !rows.Any(a => a.Id == id && !a.IsUnlocked));
        var list = new StackPanel { Spacing = 6 };
        foreach (var a in shown.Take(300))
            list.Children.Add(Row(a.Id, a.Name + (a.GlobalPercent is { } p ? $"  ·  {p:0.#}%" : ""), a.Description, a.IsUnlocked, !a.IsUnlocked && model.CanInteract));
        return ListLayout($"{rows.Count(a => a.IsUnlocked)} / {rows.Length} unlocked", list,
            Button($"Unlock selected ({selected.Count})", async () => { var pick = rows.Where(a => selected.Contains(a.Id)).ToArray(); selected.Clear(); await model.UnlockOverlaySteamAsync(pick); }, selected.Count > 0 && model.CanInteract),
            Button("Unlock all", async () => { selected.Clear(); await model.UnlockOverlaySteamAsync(rows.Where(a => !a.IsUnlocked)); }, model.CanInteract && rows.Any(a => !a.IsUnlocked)));
    }

    private Control Row(string id, string title, string description, bool unlocked, bool selectable)
    {
        var check = new CheckBox { IsChecked = selected.Contains(id), IsEnabled = selectable, IsVisible = !unlocked, VerticalAlignment = VerticalAlignment.Center };
        check.IsCheckedChanged += (_, _) => { if (check.IsChecked == true) selected.Add(id); else selected.Remove(id); refreshDebounce.Stop(); refreshDebounce.Start(); };
        return new Border
        {
            Background = unlocked ? cardDone : Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6),
            Child = new DockPanel { Children = {
                Docked(check, Dock.Left),
                new StackPanel { Spacing = 2, Margin = new Thickness(6, 0, 0, 0), Children = {
                    new TextBlock { Text = (unlocked ? "✓ " : "") + title, FontWeight = FontWeight.SemiBold, Foreground = Text, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Muted } } } } }
        };
    }

    private Control ListLayout(string summary, Control list, params Button[] actions) => new DockPanel { Children = {
        Docked(new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10), Children = {
            new TextBlock { Text = summary, Foreground = Muted },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { actions[0], actions[1] } } } }, Dock.Top),
        new ScrollViewer { Content = list } } };

    // ---- Library search ----

    private Control LibraryTab()
    {
        var results = new StackPanel { Spacing = 6 };
        var text = librarySearch.Text ?? "";
        OverlayPlatform? platform = libraryPlatform.SelectedIndex switch { 1 => OverlayPlatform.Xbox, 2 => OverlayPlatform.Steam, _ => null };
        foreach (var result in model.SearchOverlayLibrary(text, platform))
        {
            var r = result;
            var kind = r.Platform == OverlayPlatform.Steam ? "Steam app" : "Xbox title";
            results.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6), Child = new DockPanel { Children = {
                Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = {
                    Button("Copy ID", async () => await CopyAsync(r.Id, $"Copied {kind} ID {r.Id}.")),
                    Button("Open", async () => await SetGameAsync(r)) } }, Dock.Right),
                new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = {
                    new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold, Foreground = Text, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{kind} · {r.Id}", FontSize = 12, Foreground = Muted } } } } } });
        }
        if (results.Children.Count == 0)
            results.Children.Add(new TextBlock { Text = text.Trim().Length == 0 ? "Type a game name or ID. Copy ID pastes straight into the title spoofer or Achievement Mirror." : "No matches in your libraries.", Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        return new ScrollViewer { Content = results };
    }

    private async Task CopyAsync(string text, string notice)
    {
        if (Clipboard == null) return;
        await Clipboard.SetTextAsync(text);
        model.Notice = notice;
    }

    // ---- Achievement Mirror ----

    private Control MirrorTab()
    {
        var mirror = model.Mirror;
        var pairs = new ComboBox { ItemsSource = mirror.Pairs.ToArray(), SelectedItem = mirror.SelectedPair, IsEnabled = mirror.CanEditPair, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MirrorPair>((p, _) => new TextBlock { Text = PairName(p) }) };
        pairs.SelectionChanged += (_, _) => { if (pairs.SelectedItem is MirrorPair p && p != mirror.SelectedPair) mirror.SelectedPair = p; };
        var panel = new StackPanel { Spacing = 10, Children = {
            new TextBlock { Text = "Pair", Foreground = Muted }, pairs,
            new Border { Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Child = new StackPanel { Spacing = 4, Children = {
                new TextBlock { Text = mirror.StateLabel, FontWeight = FontWeight.SemiBold, Foreground = Text },
                new TextBlock { Text = $"{mirror.SourceHeader} → {mirror.TargetHeader} · {mirror.MatchSummary}", Foreground = Muted, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = mirror.StopReason, Foreground = Muted, TextWrapping = TextWrapping.Wrap, IsVisible = mirror.StopReason.Length > 0 },
                new TextBlock { Text = mirror.PendingText, Foreground = Muted, TextWrapping = TextWrapping.Wrap } } } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = {
                Button(mirror.StartStopLabel, async () => await mirror.StartStopAsync(), mirror.SelectedPair != null),
                Button("Check now", async () => await mirror.CheckNowAsync(), mirror.IsMonitoring),
                Button("Send pending", async () => await mirror.ResumeSendPendingAsync(), mirror.IsStopped && mirror.PendingCount > 0),
                Button("Discard pending", async () => await mirror.ResumeDiscardPendingAsync(), mirror.IsStopped && mirror.PendingCount > 0) } },
            new TextBlock { Text = "Create and edit pairs in the Achievement Mirror page of the main app.", FontSize = 12, Foreground = Faint, TextWrapping = TextWrapping.Wrap } } };
        foreach (var entry in mirror.LogEntries.Reverse().Take(8))
            panel.Children.Add(new TextBlock { Text = entry.ToString(), FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        return new ScrollViewer { Content = panel };
    }

    private static string PairName(MirrorPair p)
    {
        var steam = p.SteamName.Length > 0 ? p.SteamName : p.SteamAppId > 0 ? $"App {p.SteamAppId}" : "Steam (not set)";
        var xbox = p.XboxName.Length > 0 ? p.XboxName : p.XboxTitleId.Length > 0 ? $"Title {p.XboxTitleId}" : "Xbox (not set)";
        return p.Direction == MirrorDirection.SteamToXbox ? $"{steam} → {xbox}" : $"{xbox} → {steam}";
    }

    // ---- Presence / spoofing ----

    // Either platform can be spoofed independently of the current game; the IDs are prefilled from it.
    private Control PresenceTab()
    {
        var xboxId = (xboxPresenceId.Text ?? "").Trim();
        var steamId = (steamPresenceId.Text ?? "").Trim();
        var xboxRunning = model.PresenceRunning;
        var steamActive = model.SteamPresenceActive;
        var xbox = new OverlayGame(OverlayPlatform.Xbox, xboxId, model.SearchOverlayLibrary(xboxId, OverlayPlatform.Xbox).FirstOrDefault(g => g.Id == xboxId)?.Name ?? $"Title {xboxId}");
        var steam = new OverlayGame(OverlayPlatform.Steam, steamId, model.SearchOverlayLibrary(steamId, OverlayPlatform.Steam).FirstOrDefault(g => g.Id == steamId)?.Name ?? $"App {steamId}");
        return new ScrollViewer { Content = new StackPanel { Spacing = 14, Children = {
            PresenceBlock("Xbox", xboxRunning ? $"Showing {model.ActiveSpoofTitle} on Xbox. {model.SpoofingStatus}" : xboxId.Length == 0 ? "Enter or paste an Xbox title ID above." : $"Ready to show {xbox.Name} on Xbox.",
                xboxRunning ? Button("Stop Xbox spoofer", () => model.StopOverlayPresence(xbox))
                            : Button("Start Xbox spoofer", async () => await model.StartOverlayPresenceAsync(xbox), model.CanStartPresence && uint.TryParse(xboxId, out _))),
            PresenceBlock("Steam", steamActive ? $"Steam presence running. {model.SteamStatus}" : steamId.Length == 0 ? "Enter or paste a Steam app ID above." : $"Ready to show {steam.Name} on Steam.",
                steamActive ? Button("Stop Steam presence", () => model.StopOverlayPresence(steam))
                            : Button("Start Steam presence", async () => await model.StartOverlayPresenceAsync(steam), model.CanInteract && long.TryParse(steamId, out _))) } } };
    }

    private Control PresenceBlock(string title, string text, Control action) => new Border { Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Child = new StackPanel { Spacing = 6, Children = {
        new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = Text },
        new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap }, action } } };

    private static Control LabeledInput(string label, TextBox input)
    {
        var text = new TextBlock { Text = label, Foreground = Muted, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { text, input } };
    }

    private static Color Shade(Color c, double factor, byte alpha) =>
        Color.FromArgb(alpha, (byte)Math.Min(255, c.R * factor), (byte)Math.Min(255, c.G * factor), (byte)Math.Min(255, c.B * factor));

    // Matches the main window: glass colour/transparency for surfaces, the app accent for highlights.
    private void ApplyTheme()
    {
        var glass = appearance.Preferences.OledMode ? Colors.Black : Color.Parse(appearance.Preferences.GlassColour);
        Background = new SolidColorBrush(Color.FromArgb(0x99, (byte)(glass.R / 3), (byte)(glass.G / 3), (byte)(glass.B / 3)));
        var accentColor = Application.Current?.Resources.TryGetResource("Accent", null, out var value) == true && value is ISolidColorBrush b ? b.Color : Color.Parse("#70C98A");
        accent.Color = accentColor;
        cardDone.Color = Color.FromArgb(0x55, accentColor.R, accentColor.G, accentColor.B);
        var oled = appearance.Preferences.OledMode;
        panel.Color = oled ? Colors.Black : Shade(glass, 1.0, 0xF0);
        card.Color = oled ? Color.Parse("#141414") : Shade(glass, 1.35, 0xFF);
        input.Color = oled ? Color.Parse("#1C1C1C") : Shade(glass, 0.7, 0xFF);
        line.Color = oled ? Color.Parse("#282828") : Shade(glass, 1.8, 0xFF);
        foreach (var box in new Control[] { achievementSearch, librarySearch, xboxPresenceId, steamPresenceId, libraryPlatform })
        {
            if (box is TemplatedControl t) { t.Background = input; t.BorderBrush = line; }
        }
        Resources["TextControlBackgroundPointerOver"] = input;
        Resources["TextControlBackgroundFocused"] = input;
        Resources["ButtonBackground"] = card;
        Resources["ButtonBorderBrush"] = line;
        Resources["CheckBoxCheckBackgroundFillChecked"] = accent;
    }

    // ---- helpers ----

    // Tile chrome is built once; only the body is re-rendered, so search boxes keep focus while typing.
    private void AddTile(Grid grid, int row, int column, string title, ContentControl body, Control? toolbar = null)
    {
        var top = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 12), Children = { new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Text } } };
        if (toolbar != null) top.Children.Add(toolbar);
        var tile = new Border { Background = Panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Margin = new Thickness(8),
            Child = new DockPanel { Children = { Docked(top, Dock.Top), body } } };
        Grid.SetRow(tile, row); Grid.SetColumn(tile, column);
        grid.Children.Add(tile);
    }

    private static T Docked<T>(T control, Dock dock) where T : Control { DockPanel.SetDock(control, dock); return control; }

    private static Control Hint(string text, Control? action = null)
    {
        var panel = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap } } };
        if (action != null) panel.Children.Add(action);
        return panel;
    }

    private static Button Button(string label, Action click, bool enabled = true)
    {
        var b = new Button { Content = label, IsEnabled = enabled };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button Button(string label, Func<Task> click, bool enabled = true)
    {
        var b = new Button { Content = label, IsEnabled = enabled };
        b.Click += async (_, _) => { try { await click(); } catch (Exception error) { OverlayLog.Write("Action failed: " + error.Message); } };
        return b;
    }

    private void PlaceOverForeground()
    {
        var screen = previousForeground != IntPtr.Zero && GetWindowRect(previousForeground, out var r)
            ? Screens.ScreenFromPoint(new PixelPoint((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2))
            : Screens.Primary;
        screen ??= Screens.Primary;
        if (screen == null) return;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
    }

    private string? ForegroundProcessName()
    {
        if (previousForeground == IntPtr.Zero) return null;
        try { GetWindowThreadProcessId(previousForeground, out var pid); using var p = Process.GetProcessById((int)pid); return p.ProcessName == Process.GetCurrentProcess().ProcessName ? null : p.ProcessName; }
        catch { return null; }
    }

    // Windows reports QUNS_RUNNING_D3D_FULL_SCREEN while a game owns the display exclusively.
    private static bool ExclusiveFullscreenRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return SHQueryUserNotificationState(out var state) == 0 && state == 3; }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
