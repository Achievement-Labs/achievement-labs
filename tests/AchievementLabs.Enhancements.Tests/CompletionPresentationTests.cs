using AchievementLabs.MultiSelect;
using AchievementLabs.Desktop;
using AchievementLabs.Core;
using System.Reflection;
using System.Text.Json;

public static class CompletionPresentationTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
        using var lockedLegacy = JsonDocument.Parse("""{"unlocked":false,"timeUnlocked":"2002-11-15T00:00:00Z"}""");
        using var earnedLegacy = JsonDocument.Parse("""{"unlocked":true,"unlockedOnline":false,"timeUnlocked":"2002-11-15T00:00:00Z"}""");
        using var placeholderLegacy = JsonDocument.Parse("""{"timeUnlocked":"2002-11-15T00:00:00Z"}""");
        Assert(!AchievementExport.LegacyUnlocked(lockedLegacy.RootElement), "Explicit locked flag overrides timestamp");
        Assert(AchievementExport.LegacyUnlocked(earnedLegacy.RootElement), "Explicit unlocked flag is authoritative");
        Assert(!AchievementExport.LegacyUnlocked(placeholderLegacy.RootElement), "Placeholder timestamp alone never proves an unlock");
        using var offlineTimestamp = JsonDocument.Parse("""{"unlockedOnline":false,"timeUnlocked":"2026-01-01T00:00:00Z"}""");
        Assert(AchievementExport.LegacyUnlocked(offlineTimestamp.RootElement), "Online false alone never discards offline timestamp progress");
        Assert(GfwlTitles.Label("1297287434","Xbox360") == "GFWL (PC)", "Fable III PC is an exact GFWL Title ID");
        Assert(GfwlTitles.Label("1297287382","Xbox360") == "Xbox360", "Fable III console remains Xbox 360");
        var knownLegacy = new AchievementLabs.Desktop.Game("1297287434","Fable III","Xbox360",2,3,30);
        AchievementLabs.Desktop.Achievement[] incomplete = [new("69","First","",10,false),new("70","Second","",20,false),new("71","Third","",30,false)];
        Assert(!DesktopModel.CanReplaceAchievementProgress(knownLegacy,incomplete), "Definition-only response cannot zero known library progress");
        Assert(DesktopModel.CanReplaceAchievementProgress(knownLegacy,[incomplete[0] with {Unlocked=true},incomplete[1] with {Unlocked=true},incomplete[2]]), "Matching earned count accepts account progress");
        var title = new SharedDlcCatalogue.Title("70", ["XboxOne"], "https://example.com/verified", [
            new("Base game", "base", [new("1","First"),new("2","Second")]),
            new("Expansion", "dlc", [new("3","Third")])]);
        CompletionMarkers.Row[] rows = [new("1","First",true),new("2","Second",true),new("3","Third",false)];
        var baseOnly = CompletionMarkers.Evaluate(title,rows,3,2,true);
        Assert(baseOnly.BaseComplete && !baseOnly.AddOnsComplete, "Base completion excludes locked DLC");
        var dlcOnly = CompletionMarkers.Evaluate(title,[new("1","First",false),new("2","Second",false),new("3","Third",true)],3,1,true);
        Assert(!dlcOnly.BaseComplete && dlcOnly.AddOnsComplete, "DLC completion independent of base");
        Assert(!dlcOnly.FullyComplete && !baseOnly.FullyComplete, "Neither DLC-only nor base-only completion is 100%");
        Assert(CompletionMarkers.Evaluate(title,[],3,3,true).FullyComplete, "Base and DLC together prove 100%");
        Assert(CompletionMarkers.Evaluate(null,[],30,30,true).FullyComplete, "Ungrouped completed game is 100%");
        Assert(!CompletionMarkers.Evaluate(null,[],30,20,true).FullyComplete, "Incomplete ungrouped game stays unhighlighted");
        Assert(!CompletionMarkers.Evaluate(null,[],0,0,true).FullyComplete, "Zero-achievement titles stay unhighlighted");
        Assert(!CompletionMarkers.Evaluate(null,[],30,30,false).FullyComplete, "Unknown progress cannot prove 100%");
        var noDlc = new SharedDlcCatalogue.Title("73",["XboxOne"],"https://example.com/base-only",[
            new("Base game","base",[new("1","First"),new("2","Second")])]);
        Assert(CompletionMarkers.Evaluate(noDlc,[],2,2,true).FullyComplete, "No-DLC game gets 100% completion");
        Assert(!CompletionMarkers.Evaluate(noDlc,[],2,1,true).FullyComplete, "Partial no-DLC game stays unhighlighted");
        Assert(!CompletionMarkers.Evaluate(null,[],30,20,true).BaseComplete, "Unknown partial list is not inferred from 1000G or row order");
        Assert(CompletionMarkers.Evaluate(null,[],30,30,true).BaseComplete, "Complete entire known list proves base completion");
        Assert(!CompletionMarkers.Evaluate(null,[],30,30,false).BaseComplete, "Unknown account progress never awards a marker");
        Assert(!CompletionMarkers.Evaluate(title,[new("1","First",true),new("2","Wrong edition name",true)],3,2,true).BaseComplete, "Names and IDs both required");
        Assert(!CompletionMarkers.Evaluate(title,[new("1","First",true),new("1","First",true),new("2","Second",true)],3,2,true).BaseComplete, "Duplicate progress IDs do not prove completion");
        Assert(!CompletionMarkers.Evaluate(title,[..rows,new("99","New unclassified achievement",true)],4,3,true).AddOnsComplete, "Unclassified definitions block DLC completion");
        var hub = new SharedDlcCatalogue.Title("71",["XboxOne"],"https://example.com/hub",[new("Game section","dlc",[new("1","Only")])],true);
        var completedHub = CompletionMarkers.Evaluate(hub,[],1,1,true);
        Assert(!completedHub.BaseComplete && completedHub.AddOnsComplete, "Hub has no invented base-game Mythic");
        Assert(GfwlTitles.Label("1112737750","Xbox360") == "GFWL (PC)", "Confirmed GFWL title labelled accurately");
        Assert(GfwlTitles.Label("1112737749","Xbox360") == "Xbox360", "Console counterpart remains console");
        Assert(GfwlTitles.Label("1297287126","Xbox360").Contains("shared"), "Shared list preserves console and GFWL identity");
        SharedDlcCatalogue.Current.Merge(JsonSerializer.Serialize(new SharedDlcCatalogue.Document(1,[title]),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var model = new DesktopModel();
        typeof(DesktopModel).GetProperty("Games")!.SetValue(model,new AchievementLabs.Desktop.Game[]{new("70","Complete","XboxOne",3,3,1500)});
        Assert(model.Games.Single().MythicVisible && model.Games.Single().CompletionBackground != "Transparent", "Both completion decorations rendered independently");
        model.ShowMythicIcon = false; model.HighlightCompletedDlcs = false;
        Assert(!model.Games.Single().MythicVisible && model.Games.Single().CompletionBackground == "Transparent", "Opt-out restores original appearance");
        model.ShowMythicIcon = true; model.HighlightCompletedDlcs = true; model.MythicColour = "#AA1122"; model.CompletedDlcColour="#223344";
        Assert(model.Games.Single().MythicColour == DesktopModel.ValidColour("#AA1122","") &&
            model.Games.Single().CompletionBackground == DesktopModel.ValidColour("#223344",""), "Personal colours applied");
        Assert(DesktopModel.ValidColour("invalid","#183A27") == "#183A27", "Invalid colours use defaults");
        var preferences = JsonSerializer.Deserialize<DesktopPreferences>(JsonSerializer.Serialize(new DesktopPreferences {
            ShowMythicIcon=false,HighlightCompletedDlcs=false,AutoRefreshDlc=false,MythicColour="#AA1122",CompletedDlcColour="#223344"}))!;
        Assert(!preferences.ShowMythicIcon && !preferences.HighlightCompletedDlcs && !preferences.AutoRefreshDlc &&
            preferences.MythicColour=="#AA1122" && preferences.CompletedDlcColour=="#223344", "Appearance and auto-refresh choices persist");
        typeof(DesktopModel).GetProperty("SelectedGame")!.SetValue(model, model.Games.Single());
        Assert(model.SelectedGame!.MythicVisible, "Achievement page receives the same completion marker as Library");
        model.ShowMythicIcon=false;
        Assert(!model.SelectedGame.MythicVisible, "Achievement page respects icon opt-out immediately");
        model.ShowMythicIcon=true; model.MythicColour="#123456";
        Assert(model.SelectedGame.MythicVisible && model.SelectedGame.MythicColour==DesktopModel.ValidColour("#123456",""), "Achievement page respects custom icon colour");
        SharedDlcCatalogue.Current.Merge(JsonSerializer.Serialize(new SharedDlcCatalogue.Document(1,[noDlc]),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        typeof(DesktopModel).GetProperty("Games")!.SetValue(model,new AchievementLabs.Desktop.Game[]{new("73","No DLC","XboxOne",2,2,1000),new("74","Ungrouped","XboxOne",2,2,1000)});
        Assert(model.Games.All(g=>g.FullyComplete && g.CompletionBackground!="Transparent"),"Completed no-DLC and ungrouped games both highlighted");
        model.HighlightCompletedDlcs=false;
        Assert(model.Games.All(g=>g.CompletionBackground=="Transparent"),"100% highlight remains optional");
        model.HighlightCompletedDlcs=true; model.CompletedDlcColour="#334455";
        Assert(model.Games.All(g=>g.CompletionBackground==DesktopModel.ValidColour("#334455","")),"100% highlight keeps saved custom colour");
        // Collapse does not remove achievements from exports or batch selection, and search reveals matches.
        var game = new AchievementLabs.Desktop.Game("70","Test","XboxOne",2,3,1000);
        typeof(DesktopModel).GetProperty("SelectedGame")!.SetValue(model,game);
        var achievements = new AchievementLabs.Desktop.Achievement[]{
            new("1","First","",500,true),new("2","Second","",500,true),new("3","Third","",500,false)};
        var state = AchievementView.For(model); state.Collapsed.Add("Base game");
        Assert(AchievementView.Transform(achievements,model).Length == 2,"Collapsed section retains one reopenable header");
        Assert(AchievementView.Transform(achievements,model,false).Length == 3,"Batch/export retains full section");
        model.Search="First";
        Assert(AchievementView.Transform(achievements,model).Length == 3,"Search expands matching section");
        model.Search="";
        typeof(DesktopModel).GetProperty("SelectedGame")!.SetValue(model,game with {Completed=3});
        Assert(AchievementView.For(model).Collapsed.Contains("Base game"),"Refresh preserves collapse state for same Title ID");
        typeof(DesktopModel).GetProperty("SelectedGame")!.SetValue(model,new AchievementLabs.Desktop.Game("72","Different","XboxOne",0,3,0));
        Assert(AchievementView.For(model).Collapsed.Count==0,"Collapse state does not leak to another title");
        Console.WriteLine("PASS: verified base/DLC completion, conservative unknown handling, exact IDs/names, duplicate and hub guards, opt-out/colour persistence, GFWL edition labels and collapsible sections. No Xbox requests.");
    }
}
