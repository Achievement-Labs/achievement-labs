using AchievementLabs.Desktop;
using AchievementLabs.Core;
using System.Reflection;

public static class GameDetailsTests
{
    static void Assert(bool value,string message) { if(!value) throw new Exception(message); }
    public static void Run()
    {
        AppContext.SetSwitch("AchievementLabs.OfflineChecks",true);
        using var model = new DesktopModel();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = typeof(DesktopModel);
        type.GetField("session",flags)!.SetValue(model,new ConnectedXboxSession("synthetic","123",""));
        var game = new AchievementLabs.Desktop.Game("572802557","Ghosts","XboxOne",0,91,0);
        type.GetProperty("SelectedGame",flags)!.SetValue(model,game);
        type.GetProperty("QueueActive",flags)!.SetValue(model,true);
        Assert(model.CanOpenGameSpoofer,"Active queue permits spoofer navigation");
        model.OpenGameSpoofer();
        Assert(model.IsSpoofer && model.PresenceTitleId == game.Id && !model.PresenceRunning,"Navigation selects exact Title ID without starting presence");
        var cache=(Dictionary<string,(decimal? Minutes,DateTimeOffset Checked)>)type.GetField("gamePlaytimeCache",flags)!.GetValue(model)!;
        cache["123/"+game.Id]=(180m,DateTimeOffset.UtcNow);
        var read=type.GetMethod("LoadGamePlaytimeAsync",flags)!;
        ((Task)read.Invoke(model,[game,0])!).GetAwaiter().GetResult();
        Assert(model.GamePlaytimeText.Contains("3 h"),"Header reuses account/title cached Xbox hours without HTTP");
        type.GetProperty("GamePlaytimeText",flags)!.SetValue(model,"newer selection");
        ((Task)read.Invoke(model,[game,-1])!).GetAwaiter().GetResult();
        Assert(model.GamePlaytimeText=="newer selection","Outdated selection cannot overwrite header");
        type.GetProperty("QueueActive",flags)!.SetValue(model,false);
        Console.WriteLine("PASS: Game spoofer navigation during queue activity, cached account/title playtime and stale selection guard. No Xbox requests.");
    }
}
