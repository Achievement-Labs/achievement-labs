using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AchievementLabs.Desktop;
using AchievementLabs.Models;

static class Xbox360LayoutChecks
{
    public static void Run(string output, string? signing = null)
    {
        AppBuilder.Configure<AchievementLabs.Desktop.App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var window=new MainWindow();
        var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        var model=(DesktopModel)typeof(MainWindow).GetField("model",flags)!.GetValue(window)!;
        typeof(DesktopModel).GetProperty("Xbox360EditorTitleName",flags)!.SetValue(model,"Xbox 360 — sample game");
        var rows=Enumerable.Range(1,24).Select(i=>new Xbox360ProfileAchievement((uint)i,i==1?"A longer achievement name to check wrapping":"Achievement "+i,"Complete the challenge to earn this achievement.","Challenge completed.",10,i%4==0?0x20000u:0,null)).ToArray();
        typeof(DesktopModel).GetProperty("Xbox360Achievements",flags)!.SetValue(model,rows);
        model.SelectedXbox360Choice=model.Xbox360Choices[0];
        if (AchievementLabs.Services.Xbox360.Xbox360SigningSession.HasBundledIdentity)
        {
            model.Xbox360SigningStorePath=Path.Combine(Path.GetFullPath(output),"no-user-signing-cache.bin");
            model.RestoreXbox360SigningData();
            if (model.Xbox360NeedsSigningSetup) throw new Exception("Standalone screen still asks for setup");
        }
        model.Navigate("Xbox360ProfileEditor");
        Directory.CreateDirectory(output);
        foreach(var size in new[]{new Size(1440,920),new Size(1120,720)})
        {
            window.Width=size.Width; window.Height=size.Height;
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var action=window.GetLogicalDescendants().OfType<Button>().Single(b=>b.Name=="UnlockXbox360Selected");
            if(action.GetLogicalAncestors().OfType<ScrollViewer>().Any()) throw new Exception("Primary unlock action must stay outside the scrolling options");
            var point=action.TranslatePoint(default,window);
            if(point==null || point.Value.Y<0 || point.Value.Y+action.Bounds.Height>size.Height) throw new Exception("Unlock action clipped at "+size);
            using var frame=window.CaptureRenderedFrame();
            if(frame==null) throw new Exception("No rendered frame");
            frame.Save(Path.Combine(output,$"xbox360-{size.Width}x{size.Height}.png"));
        }
        if (signing != null)
        {
            AchievementLabs.Services.Xbox360.Xbox360SigningSession.LoadHorizonFile(signing);

            model.Xbox360Choices[0].Selected=true;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var ready=window.CaptureRenderedFrame(); ready!.Save(Path.Combine(output,"xbox360-ready-1120x720.png"));
        }
        window.Close(); model.Dispose();
        Console.WriteLine("PASS: compact Xbox 360 screen rendered at 1440x920 and 1120x720; unlock action remains visible. Synthetic data only.");
    }
}
