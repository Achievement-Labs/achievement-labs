using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AchievementLabs.Desktop;

/// <summary>Native Windows alerts for the unpackaged desktop app.</summary>
internal static class WindowsNotifications
{
    private static bool registered;
    public static bool TryShow(string title, string message)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!registered)
            {
                ToastNotificationManagerCompat.OnActivated += _ => Dispatcher.UIThread.Post(() =>
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
                    {
                        window.Show();
                        window.WindowState = Avalonia.Controls.WindowState.Normal;
                        window.Activate();
                    }
                });
                registered = true;
            }
            new ToastContentBuilder().AddText(title).AddText(message).Show(toast =>
                toast.ExpirationTime = DateTimeOffset.Now.AddHours(1));
            return true;
        }
        catch { return false; }
    }
}
