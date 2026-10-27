using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private async void ImportPcPresence(object? sender, RoutedEventArgs args)
    {
        if (!OperatingSystem.IsMacOS() || !model.CanQuery || model.PresenceRunning) return;
        byte[]? bytes = null;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import encrypted PC sign-in", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Achievement Labs PC sync") { Patterns = ["*.labslink"] }]
            });
            if (files.Count != 1) return;
            await using (var stream = await files[0].OpenReadAsync())
            {
                var buffer = new byte[131073];
                try
                {
                    var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
                    if (count is 0 or > 131072) throw new InvalidDataException();
                    bytes = buffer.AsSpan(0, count).ToArray();
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
            }
            var dialog = new Window { Title = "Pair PC authorization", Width = 580, Height = 380,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var key = new TextBox { PlaceholderText = "Separate pairing key from PC", PasswordChar = '●', MaxLength = 43 };
            var confirm = new Button { Content = "Verify and use for spoofer" };
            var cancel = new Button { Content = "Cancel" };
            var paste = new Button { Content = "Paste key" };
            paste.Click += async (_, _) =>
            {
                try
                {
                    var text = Clipboard == null ? null : await Clipboard.TryGetTextAsync();
                    if (text?.Trim() is { Length: 43 } value) key.Text = value;
                    else key.PlaceholderText = "Copy the 43-character pairing key on PC first";
                }
                catch { key.PlaceholderText = "Could not read clipboard; paste the key with ⌘V"; }
            };
            confirm.Click += (_, _) => dialog.Close(key.Text);
            cancel.Click += (_, _) => dialog.Close();
            dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
                new TextBlock { Text = "Use PC authorization for this Mac session?", FontSize = 21, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Only the same account is accepted. Pairing verifies your profile; it does not start spoofing. The PC authorization stays in memory until removed, disconnected or the app closes. Event tokens and your Mac sign-in are retained.", TextWrapping = TextWrapping.Wrap },
                key, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { paste, confirm, cancel } }
            } };
            string? entered;
            try { entered = await dialog.ShowDialog<string?>(this); }
            finally { key.Text = ""; }
            if (entered == null) return;
            var imported = model.ReadPcPresence(bytes, entered.Trim());
            entered = null;
            await model.ImportPcPresenceAsync(imported);
        }
        catch { model.Notice = "Could not pair PC authorization. Check the key, use a file exported within 10 minutes, and connect the same Xbox account. The existing sign-in was retained."; }
        finally { if (bytes != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private void RemovePcPresence(object? sender, RoutedEventArgs args) => model.ClearPcPresence();
    private async void RememberMacSignIn(object? sender, RoutedEventArgs args) => await model.ApplyMacRememberSignInAsync();
    private void ComparePcPresenceMetadata(object? sender, RoutedEventArgs args) => model.ComparePcPresenceMetadata();
}
