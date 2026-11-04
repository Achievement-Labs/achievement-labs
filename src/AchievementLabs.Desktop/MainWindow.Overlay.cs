using Avalonia.Threading;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private OverlayHotkey? overlayHotkey;
    private OverlayWindow? overlay;

    // Prototype: Windows only; Mac/Linux builds keep their existing behaviour.
    private void StartOverlay()
    {
        if (!OperatingSystem.IsWindows() || AppContext.TryGetSwitch("AchievementLabs.OfflineChecks", out var offline) && offline) return;
        overlayHotkey = OverlayHotkey.TryStart(
            () => Dispatcher.UIThread.Post(() => (overlay ??= new OverlayWindow(model, Presentation.AppearanceController.For(this), visible => overlayHotkey?.SetEscapeEnabled(visible))).Toggle()),
            () => Dispatcher.UIThread.Post(() => overlay?.HideOverlay()));
        if (overlayHotkey is { Registered: false }) model.Notice = "Overlay hotkey Alt+U is already in use by another app.";
        Closed += (_, _) => { overlayHotkey?.Dispose(); overlay?.Close(); };
    }
}
