using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AchievementLabs.Desktop;
using AchievementLabs.Desktop.Workflows;

static class AchievementDescriptionLayoutChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        AppBuilder.Configure<AchievementLabs.Desktop.App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing=false }).SetupWithoutStarting();
        var window=new MainWindow();
        var model=(DesktopModel)typeof(MainWindow).GetField("model",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        var all=window.GetLogicalDescendants().ToArray();
        var achievements=all.OfType<ListBox>().Single(l=>l.Name=="AchievementLabsAchievementList");
        typeof(DesktopModel).GetField("achievements",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(model,new[]{new Achievement("1","A longer achievement name to check wrapping","Complete a series of challenges across the entire game to earn this achievement.",50,false)});
        typeof(DesktopModel).GetProperty("SelectedGame")!.SetValue(model,new AchievementLabs.Desktop.Game("7","Sample game","Xbox One",0,1,0));
        model.Filter("All");
        var queue=all.OfType<ListBox>().Single(l=>l.Name=="XboxAutoUnlockList");
        queue.ItemsSource=new[]{new AutoUnlockerViewModel.AutoUnlockQueueDisplay {AchievementName="A longer achievement name to check wrapping",AchievementDescription="Complete a series of challenges across the entire game to earn this achievement.",DelayMinutes=9999.5m,CanEditDelay=true,Status="Pending"}};
        foreach(var size in new[]{new Size(1920,1080),new Size(1120,720)})
        foreach(var page in new[]{"Achievements","Spoofer"})
        {
            window.Width=size.Width;window.Height=size.Height;queue.MaxWidth=size.Width==1120?660:double.PositiveInfinity;model.Navigate(page);window.Show();
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var list=page=="Achievements"?achievements:queue;
            var name=page=="Achievements"?"AchievementRowDescription":"AutoUnlockRowDescription";
            var description=list.GetLogicalDescendants().OfType<TextBlock>().Single(t=>t.Name==name);
            if(!description.Text!.StartsWith("Complete a series") || description.Bounds.Width<100 || description.Bounds.Height<10)
                throw new Exception("Description missing or clipped: "+page+" "+size);
            var row=(Grid)description.Parent!;
            if(size.Width==1920 && Grid.GetRow(description)!=0 || size.Width==1120 && Grid.GetRow(description)!=1)
                throw new Exception("Description did not adapt to window width: "+page+" "+size);
            if(page=="Spoofer" && row.GetLogicalDescendants().OfType<NumericUpDown>().Single().Bounds.Width<160)
                throw new Exception("Description squeezed the delay control.");
            using var frame=window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(output,$"{page}-{size.Width}.png"));
        }
        window.Close();model.Dispose();
        Console.WriteLine("PASS: actual achievement and queue descriptions wrap and adapt at 1920 and 1120 pixels; four-digit delay stays wide. Synthetic data only.");
    }
}

