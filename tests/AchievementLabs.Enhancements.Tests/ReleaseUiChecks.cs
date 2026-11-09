using System.Reflection;
using AchievementLabs.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

static class ReleaseUiChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var window = new MainWindow(); window.Show();
        void Pump() { for (int i = 0; i < 15; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); } }
        void Render(Window target, string name)
        {
            Pump();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)target.ClientSize.Width, (int)target.ClientSize.Height), new Vector(96, 96));
            bitmap.Render(target); bitmap.Save(Path.Combine(output, name));
        }
        Pump();
        foreach (var name in new[] { "EaSidebar", "GogSidebar" })
        {
            var expander = window.FindControl<Expander>(name)!;
            Check(expander.Header is StackPanel panel && panel.Children.OfType<Avalonia.Controls.Image>().Single().Source != null, name + " logo must decode from packaged resource");
        }
        Check(!window.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("NATIVE DEVELOPMENT") == true), "Development footer removed");
        var model = (DesktopModel)window.DataContext!;
        Check(!model.BuildVersionLabel.EndsWith(".0"), "Display version hides the zero revision");
        Render(window, "release-home.png");
        foreach (var ready in new[] { false, true })
        {
            var method = typeof(MainWindow).GetMethod("PromptAppUpdateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task<bool>)method.Invoke(window, new object[] { "1.3.3", ready })!;
            Pump();
            var dialog = window.OwnedWindows.Single(w => w.Name == "AppUpdatePrompt");
            Check(dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "AcceptAppUpdate").Content?.ToString() == (ready ? "Install and restart" : "Download update"), "Prompt action");
            Render(dialog, ready ? "update-ready.png" : "update-available.png");
            dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "DeferAppUpdate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(); Check(task.IsCompletedSuccessfully && !task.Result, "Later dismisses the prompt");
        }
        window.Close();
        Console.WriteLine("PASS: packaged EA/GOG logos, footer removal, version display, update and verified-install dialogs; Later works. Headless rendering; no network/account actions.");
    }
}
