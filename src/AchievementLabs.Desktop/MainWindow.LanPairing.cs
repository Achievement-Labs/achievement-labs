using AchievementLabs.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private Window? lanPairingWindow;
    private async Task<LanPairing.Approval> ConfirmLanPairingAsync(Window owner, LanPairing.Review review)
    {
        var dialog = new Window { Title = "Confirm local pairing", Width = 480, Height = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var remember = new CheckBox { Content = "Remember this device for this Xbox account", IsChecked = review.Remembered };
        var accept = new Button { Content = review.Remembered ? "Approve transfer" : "Codes match — pair" };
        var decline = new Button { Content = "Cancel" };
        accept.Click += (_, _) => dialog.Close(new LanPairing.Approval(true, remember.IsChecked == true));
        decline.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
            new TextBlock { Text = review.Remembered ? "Recognized paired device" : "Compare this code on BOTH devices", FontSize = 20, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = review.Code, FontSize = 36, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = review.Remembered ? "Approve to transfer fresh PC authorization. Nothing starts automatically."
                : "Only approve if the other app shows exactly the same code. No account authorization is sent until both devices approve.", TextWrapping = TextWrapping.Wrap },
            remember, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { accept, decline } }
        } };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        timer.Tick += (_, _) => dialog.Close();
        timer.Start();
        try { return await dialog.ShowDialog<LanPairing.Approval?>(owner) ?? new(false, false); }
        finally { timer.Stop(); }
    }

    private async void HostLanPairing(object? sender, RoutedEventArgs args)
    {
        if (!OperatingSystem.IsWindows() || !model.CanQuery) return;
        if (lanPairingWindow != null) { lanPairingWindow.Activate(); return; }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var dialog = new Window { Title = "Pair a Mac on this network", Width = 580, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        lanPairingWindow = dialog;
        var status = new TextBlock { Text = "Preparing local pairing…", TextWrapping = TextWrapping.Wrap };
        var diagnostics = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var discoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => cancellation.Cancel();
        dialog.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 18, Children = {
            new TextBlock { Text = "On Mac: Settings → Sync → Pair with Windows", FontSize = 21, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Use the same local network and Xbox account. If Windows asks, allow this app on your private network. Pairing closes after one attempt or two minutes.", TextWrapping = TextWrapping.Wrap },
            status, diagnostics, close } } };
        LanPairing.Host? host = null;
        LanPairingStore? store = null;
        Task? transfer = null;
        try
        {
            var account = model.LanPairingAccount;
            var generation = model.LanPairingSession;
            store = new LanPairingStore(); host = new LanPairing.Host();
            discoveryTimer.Tick += (_, _) => diagnostics.Text = host.DiscoveryStatus;
            discoveryTimer.Start();
            status.Text = "Waiting for Mac…\nManual address: " + string.Join("  /  ", LanPairing.LocalAddresses().Select(a => $"{a}:{host.Port}"));
            async Task RunAsync()
            {
                try
                {
                    await host.SendAsync(store.Identity, fp => store.IsTrusted(fp, account),
                        review => ConfirmLanPairingAsync(dialog, review),
                        () => generation == model.LanPairingSession ? model.CreateLanPresenceTransferAsync(account, cancellation.Token) : throw new InvalidOperationException(),
                        (fp, remember) => { if (remember) store.Remember(fp, account); }, cancellation.Token);
                    status.Text = "Paired successfully. Start spoofing from Game Session on your Mac. The local listener is closed.";
                }
                catch (OperationCanceledException) { status.Text = "Pairing cancelled or timed out. Close and reopen to try again."; }
                catch { status.Text = "Pairing did not complete. Keep the Xbox app signed in to the same account; check the network, then reopen this window. No diagnostic credential values were logged."; }
                finally { discoveryTimer.Stop(); diagnostics.Text = host.DiscoveryStatus; host.Dispose(); }
            }
            transfer = RunAsync();
            await dialog.ShowDialog(this);
        }
        catch { model.Notice = "Could not start local pairing. Close another pairing window or check local network access and secure storage."; }
        finally
        {
            discoveryTimer.Stop(); cancellation.Cancel(); host?.Dispose();
            if (transfer != null) await transfer;
            store?.Dispose(); lanPairingWindow = null;
        }
    }

    private async void ConnectLanPairing(object? sender, RoutedEventArgs args)
    {
        if (!AchievementLabs.Core.DesktopPlatform.IsUnixDesktop || !model.CanQuery || model.PresenceRunning) return;
        if (lanPairingWindow != null) { lanPairingWindow.Activate(); return; }
        using var cancellation = new CancellationTokenSource();
        var dialog = new Window { Title = "Pair with Windows", Width = 600, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        lanPairingWindow = dialog;
        var status = new TextBlock { Text = "On Windows, open Settings → Sync → Pair a Mac on this network.", TextWrapping = TextWrapping.Wrap };
        var devices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Select your Windows app" };
        var address = new TextBox { PlaceholderText = "Manual fallback: 192.168.1.10:12345" };
        devices.SelectionChanged += (_, _) => { if (devices.SelectedItem != null) address.Text = ""; };
        var diagnostics = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var find = new Button { Content = "Find Windows app" };
        var connect = new Button { Content = "Pair selected PC" };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => cancellation.Cancel();
        dialog.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
            new TextBlock { Text = "Pair with Windows", FontSize = 24 },
            new TextBlock { Text = "No files or pairing keys. Keep both apps open on the same local network, using the same Xbox account.", TextWrapping = TextWrapping.Wrap },
            status, diagnostics, devices, find, address,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { connect, close } }
        } } };
        var account = model.LanPairingAccount;
        var generation = model.LanPairingSession;
        Task? operation = null;
        async Task Discover()
        {
            find.IsEnabled = connect.IsEnabled = false; status.Text = "Looking for Windows apps…";
            try
            {
                using var store = new LanPairingStore();
                var found = await LanPairing.DiscoverAsync(cancellation.Token, store.KnownAddresses(account),
                    text => Dispatcher.UIThread.Post(() => diagnostics.Text = text));
                devices.ItemsSource = found; devices.SelectedIndex = found.Count == 1 ? 0 : -1;
                status.Text = found.Count == 0 ? "No Windows app found. Keep its pairing window open. UDP discovery may be blocked even when manual pairing works. Pair manually with Remember enabled on both devices to save its address for future direct searches." : "Choose your PC, then compare the code on both devices.";
            }
            catch (OperationCanceledException) { }
            catch { status.Text = "Discovery unavailable. Allow local network access, or enter the address shown on Windows."; }
            finally { find.IsEnabled = connect.IsEnabled = true; }
        }
        find.Click += async (_, _) => { operation = Discover(); await operation; };
        connect.Click += async (_, _) =>
        {
            async Task Pair()
            {
                find.IsEnabled = connect.IsEnabled = false;
                try
                {
                    var candidate = devices.SelectedItem as LanPairing.Candidate;
                    if (!string.IsNullOrWhiteSpace(address.Text))
                    {
                        var parts = address.Text.Trim().Split(':');
                        if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) throw new InvalidDataException();
                        candidate = new LanPairing.Candidate(parts[0], port, "");
                    }
                    if (candidate == null) { status.Text = "Select your Windows app or enter its displayed address."; return; }
                    status.Text = "Connecting securely…";
                    using var store = new LanPairingStore();
                    var result = await LanPairing.ReceiveAsync(candidate, store.Identity, fp => store.IsTrusted(fp, account),
                        review => ConfirmLanPairingAsync(dialog, review), async (transfer, token) =>
                        {
                            if (account != model.LanPairingAccount || generation != model.LanPairingSession || token.IsCancellationRequested) throw new InvalidDataException();
                            var imported = model.ReadPcPresence(transfer.Bytes, transfer.PairingKey);
                            await model.ImportPcPresenceAsync(imported, token);
                        }, cancellation.Token);
                    if (result.Remember) store.Remember(result.PeerFingerprint, account, candidate.Address);
                    status.Text = "Paired successfully. Open Game Session to start spoofing. Device recognition is remembered only if both devices selected it.";
                }
                catch (OperationCanceledException) { status.Text = "Pairing cancelled or timed out. Reopen pairing on Windows to try again."; }
                catch { status.Text = "Could not pair. Check the address and Xbox account, stop spoofing, and reopen pairing on Windows. Existing sign-in retained."; }
                finally { find.IsEnabled = connect.IsEnabled = true; }
            }
            operation = Pair(); await operation;
        };
        dialog.Opened += async (_, _) => { operation = Discover(); await operation; };
        try { await dialog.ShowDialog(this); }
        finally { cancellation.Cancel(); if (operation != null) await operation; lanPairingWindow = null; }
    }

    private void ForgetLanPairing(object? sender, RoutedEventArgs args)
    {
        if (lanPairingWindow != null) { model.Notice = "Close the pairing window before forgetting devices."; return; }
        try { LanPairingStore.ForgetAll(); model.Notice = "Local device identity and remembered pairing approvals removed. The next connection requires code comparison."; }
        catch { model.Notice = "Could not remove remembered devices from secure storage."; }
    }
}
