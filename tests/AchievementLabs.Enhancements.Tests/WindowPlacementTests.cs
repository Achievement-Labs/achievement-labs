using AchievementLabs.Core;
using System.Text.Json;

public static class WindowPlacementTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var saved = new WindowPlacement(1200,800,150,100);
        var restored = WindowPlacement.Fit(JsonSerializer.Deserialize<WindowPlacement>(JsonSerializer.Serialize(saved))!,0,0,1920,1040,1,16,40);
        Assert(restored==saved,"Custom size and screen position survive save/restore");
        var tooLow = WindowPlacement.Fit(new(1200,1000,150,300),0,0,1920,1040,1,16,40);
        Assert(tooLow.Y+tooLow.Height+40<=1040,"Window frame is kept above the taskbar");
        var highDpi = WindowPlacement.Fit(new(1440,920,3000,2000),0,0,1920,1040,2,16,40);
        Assert(highDpi.X+(highDpi.Width+16)*2<=1920 && highDpi.Y+(highDpi.Height+40)*2<=1040,"Physical working-area constraints apply on high DPI screens below normal minimum");
        var second = WindowPlacement.Fit(new(1200,800,-1600,100),-1920,0,1920,1040,1,16,40);
        Assert(second.X==-1600 && second.Y==100,"Secondary monitor with negative desktop coordinates preserved");
        var removed = WindowPlacement.Fit(new(1200,800,-3200,-2000),0,0,1920,1040,1,16,40);
        Assert(removed.X==0 && removed.Y==0,"Removed monitor placement is recovered on available screen");
        var legacy=JsonSerializer.Deserialize<WindowPlacement>("{\"Width\":1200,\"Height\":800}")!;
        var centred=WindowPlacement.Fit(legacy,0,0,1920,1040,1,16,40);
        Assert(centred.X==352 && centred.Y==100,"Old size-only preferences migrate to centred working-area placement");
        var bad=WindowPlacement.Fit(new(double.NaN,double.PositiveInfinity,int.MaxValue,int.MinValue),0,0,1920,1040,1,16,40);
        Assert(double.IsFinite(bad.Width) && double.IsFinite(bad.Height) && bad.X>=0 && bad.Y>=0,"Invalid saved dimensions cannot strand the window");
        Console.WriteLine("PASS: persisted window position, taskbar/frame bounds, high DPI, negative-coordinate monitors, removed monitors and size-only migration.");
    }
}
