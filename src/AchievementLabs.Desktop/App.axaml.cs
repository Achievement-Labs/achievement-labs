using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AchievementLabs.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        MacStartup.Stage("Loading application theme");
        AvaloniaXamlLoader.Load(this);
        MacStartup.Stage("Application theme loaded");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MacStartup.Stage("Constructing desktop model and window");
            desktop.MainWindow = new MainWindow();
            MacStartup.Stage("Window constructed");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
