using System.Reflection;
using AchievementLabs.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;

static class OverlayCompatibilityChecks
{
    public static void Run(string output)
    {
        AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var owner = new MainWindow();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var model = (DesktopModel)typeof(MainWindow).GetField("model", flags)!.GetValue(owner)!;
        if (typeof(MainWindow).GetField("overlayHotkey", flags)!.GetValue(owner) != null) throw new Exception("Offline check registered a global hotkey.");
        var type = typeof(MainWindow).Assembly.GetType("AchievementLabs.Desktop.OverlayWindow")!;
        var overlay = (Window)Activator.CreateInstance(type, model, AchievementLabs.Desktop.Presentation.AppearanceController.For(owner), (Action<bool>)(_ => { }))!;
        type.GetMethod("Render", flags | BindingFlags.DeclaredOnly)!.Invoke(overlay, null);
        overlay.Width = 1280; overlay.Height = 850; overlay.Show();
        Dispatcher.UIThread.RunJobs(); overlay.UpdateLayout();
        var labels = overlay.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        foreach (var label in new[] { "Current game", "Library search", "Achievement Mirror", "Presence", "Alt+U or Esc to close" })
            if (!labels.Contains(label)) throw new Exception("Overlay tile missing: " + label);
        Directory.CreateDirectory(output);
        using var frame = overlay.CaptureRenderedFrame();
        if (frame == null) throw new Exception("Overlay did not render.");
        frame.Save(Path.Combine(output, "overlay-offline.png"));
        overlay.Close(); owner.Close(); model.Dispose();
        Console.WriteLine("PASS: four overlay tiles and Alt+U/Esc hints render with combined features; offline startup registers no global hotkey. No detection, unlock, Mirror or presence actions invoked.");
    }
}
