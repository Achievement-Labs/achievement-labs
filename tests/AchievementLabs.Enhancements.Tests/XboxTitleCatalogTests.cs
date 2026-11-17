using AchievementLabs.Core;
using AchievementLabs.Desktop;
using System.Diagnostics;

static class XboxTitleCatalogTests
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    public static async Task RunAsync()
    {
        AppContext.SetSwitch("AchievementLabs.OfflineChecks",true);
        var allocated=GC.GetTotalAllocatedBytes(true);var timer=Stopwatch.StartNew();
        Check(XboxTitleCatalog.Count>=14000,"Full known title catalog embedded");
        Check(XboxTitleCatalog.Find("1999574456")!.TotalAchievements==99,"Forza Motorsport 7 uses full total, not the 62-rule subset");
        Check(XboxTitleCatalog.Find("1297287434")!.Platforms.Contains("GFWL"),"Fable III PC is GFWL");
        Check(!XboxTitleCatalog.Find("1297287382")!.Platforms.Contains("GFWL"),"Console counterpart is not folded into PC");
        Check(XboxTitleCatalog.Find("1297292157")!.Platforms.Contains("Windows8"),"Spartan Assault legacy PC edition is Windows 8");
        Check(DesktopModel.CatalogGame("1297292157").Platform.Contains("Windows8"),"Opening a search result retains Windows 8 service identity");
        Check(DesktopModel.UsesLegacyEndpoint(DesktopModel.CatalogGame("1297292157")),"Legacy Windows routes to legacy reader");
        Check(XboxTitleCatalog.SearchEntries("Halo").Select(t=>t.TitleId).Distinct().Count()>10,"Separate title editions remain searchable");
        Check(TitleSearchPlatforms.Classify(["iOS"]).Contains("ios") && !TitleSearchPlatforms.Classify(["iOS"]).Contains("android"),"iOS separated from Android");
        Check(TitleSearchPlatforms.Classify(["Web"]).SetEquals(["web"]),"Web is a separate platform");
        Check(TitleSearchPlatforms.Classify(["Xbox360","GFWL"]).SetEquals(["360","gfwl"]),"Verified shared lists match both platform filters");
        using var model=new DesktopModel { TitleSearch="Halo" };
        await model.SearchTitlesAsync();
        Check(model.TitleSearchResults.Length>10 && model.LookupOutput.Contains("no Xbox or DBox"),"Main title search uses local catalog without per-result verification");
        model.TitleSearch="1297292157";await model.SearchTitlesAsync();
        Check(model.TitleSearchResults.Any(t=>t.Platform.Contains("Windows 8")),"Main search labels legacy Windows explicitly");
        foreach (var query in new[] { "Modern Combat 5", "1744505425", "1195776867" })
        {
            model.TitleSearch = query;
            await model.SearchTitlesAsync();
            Check(model.TitleSearchResults.Length == 1 &&
                model.TitleSearchResults[0] is { TitleId: "1195776867", Platform: "Windows 8" },
                "Modern Combat 5 name and either ID show a single legacy Windows 8 result");
        }
        Check(DesktopModel.ResolveWindows8TitleId("1744505425") == "1195776867" &&
            DesktopModel.CatalogGame("1195776867").Platform == "Windows8",
            "Opening either Modern Combat 5 ID uses the Windows 8 achievement route");
        Check(XboxTitleCatalog.SearchEntries("1656611287").Select(t => t.TitleId).SequenceEqual(["826940249"]),
            "An alias taken from the snapshot resolves to the original ExZeus 2 ID");
        var editions = XboxTitleCatalog.SearchEntries("1653696130");
        Check(editions.Length > 1 && editions.All(t => t.TitleId != "1653696130") &&
            XboxTitleCatalog.ResolveWindows8TitleId("1653696130") == "1653696130",
            "An ambiguous alias shows separate editions without guessing an achievement route");
        var temp=Path.Combine(Path.GetTempPath(),"labs-external-spools-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {File.WriteAllText(Path.Combine(temp,"fixture.spool"),"synthetic");model.UbisoftSpoolSourceDirectory=temp;Check(model.UbisoftSpools.Length==1,"External spool library remains usable");}
        finally{Directory.Delete(temp,true);}
        timer.Stop();
        Console.WriteLine($"PASS: {XboxTitleCatalog.Count} database-derived titles, exact editions, Windows 8/GFWL, mobile/Web filtering, local main search and external spools. {timer.ElapsedMilliseconds} ms; {GC.GetTotalAllocatedBytes(true)-allocated} allocated bytes. No network.");
    }
}
