using AchievementLabs.Desktop;
using AchievementLabs.Desktop.Workflows;
using AchievementLabs.MultiSelect;
using Newtonsoft.Json.Linq;

public static class UITweakTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        foreach (var invalid in new[] { "", "NaN", "Infinity", "-1", "101", "unknown" }) Check(AchievementRarity.Parse(invalid) == null, "Invalid rarity must stay unknown.");
        Check(AchievementRarity.Parse("0") == 0 && AchievementRarity.Parse("12.34") == 12.34 && AchievementRarity.Parse("100") == 100, "Xbox percentages are percentages, not fractions.");
        Check(new Achievement("1","A","",0,false) { GlobalUnlockPercentage = 12.34 }.GlobalUnlockLabel == "12.34%", "Rarity display.");
        Check(new Achievement("1","A","",0,false).GlobalUnlockLabel == "—", "Unknown rarity is not zero.");
        var rarityRows = new[] { new Achievement("unknown","Unknown","",0,false), new Achievement("common","Common","",0,false){GlobalUnlockPercentage=90}, new Achievement("rare","Rare","",0,false){GlobalUnlockPercentage=0} };
        Check(AchievementView.Arrange(rarityRows,false,3,"All packs").Cast<Achievement>().Select(a=>a.Id).SequenceEqual(new[]{"rare","common","unknown"}),"Global % ascending keeps unknown last and includes zero.");
        Check(AchievementView.Arrange(rarityRows,false,4,"All packs").Cast<Achievement>().Select(a=>a.Id).SequenceEqual(new[]{"common","rare","unknown"}),"Global % descending keeps unknown last.");
        Check(new AchievementLabs.Core.DesktopPreferences().AutoMaximize,"Fresh installs default to maximized.");
        Check(System.Text.Json.JsonSerializer.Deserialize<AchievementLabs.Core.DesktopPreferences>("{\"AutoMaximize\":false}")!.AutoMaximize==false,"An explicit saved off setting remains respected.");
        using var model = new DesktopModel();
        model.Navigate("SteamQueue"); Check(model.IsSteamQueue && model.IsSteam && !model.IsQueues, "Steam queue has an independent page.");
        model.Navigate("Legacy");Check(model.IsSteamQueue,"Removed Legacy tools page cannot be opened.");
        model.OpenLibraryStats(new AchievementLabs.Desktop.Game("322252289","Fixture","XboxOne",0,1,0));
        Check(model.IsStats && model.StatsEditor.TitleId == "322252289", "Library stat shortcut preserves the selected Title ID.");
        model.StatsTitleId="309062877";Check(model.StatsEditor.TitleId=="309062877" && model.StatsEditor.StatModeLabel.Contains("Title-based"),"Top stat title field updates support classification.");
        model.SteamQueue.CompletedCount=2;model.SteamQueue.QueuedCount=3;Check(model.SteamQueue.ProgressText.StartsWith("2 / 5"),"Steam progress denominator includes completed entries.");
        var root=Path.Combine(Path.GetTempPath(),"labs-ui-tweaks-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var data=Path.Combine(root,"Data.json"); File.WriteAllText(data,"{\"SupportedTitleIDs\":[]}");
            var stats=Path.Combine(root,"Stats.json"); File.WriteAllText(stats,"{}");
            var account=new NativeAccountContext{XAUTH="synthetic",XUIDOnly="123",EventsToken="x:XBL3.0 x=synthetic;event",EventsDirectory=root};
            var vm=new StatsEditorViewModel(new NativeNotices(_=>{}),account,_=>throw new Exception("Support checks must not request Xbox data."),false){TitleId="322252289"};
            vm.RefreshSupport(); Check(vm.StatModeLabel.Contains("Event-based") && vm.StatSupportLabel.Contains("Not Supported"),"Missing event mapping is unsupported.");
            File.WriteAllText(stats,new JObject{["322252289"]=new JObject{["Stats"]=new JObject{["Count"]=new JObject{["ValueReplacement"]=new JObject{["ReplacementType"]="Replace",["Target"]="REPLACEVALUE",["Replacement"]="REPLACEVALUE"}}}}}.ToString());
            File.WriteAllText(Path.Combine(root,"322252289.json"),"{\"data\":{\"baseData\":{\"titleId\":\"322252289\",\"properties\":{\"count\":REPLACEVALUE}}}}");
            vm.RefreshSupport(); Check(vm.StatSupportLabel.StartsWith("Supported"),"Stats.json and standalone template establish mapped support.");
            vm.LoadStats().GetAwaiter().GetResult(); Check(vm.StatItems.Single().IsEditable,"Mapped event stats load without title-stat API calls.");
            var row=vm.StatItems.Single(); row.NewValue="invalid-json"; vm.UpdateSingleStat(row).GetAwaiter().GetResult(); Check(row.EditStatus=="Error","Invalid event payload is rejected before sending.");
            vm.TitleId="309062877"; Check(vm.StatModeLabel.Contains("Title-based"),"Title-based stat mode is separate from achievement type.");
            row.NewValue="1";vm.UpdateSingleStat(row).GetAwaiter().GetResult();Check(row.EditStatus=="Reload required","Stale event stat row cannot submit after title change.");
            Check(File.ReadAllText(data)=="{\"SupportedTitleIDs\":[]}","Reading support never edits achievement catalogue.");
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine("PASS: global rarity, independent Steam navigation, library stats shortcut, title/event stats support from Stats.json, missing templates, invalid payload rejection and catalogue preservation.");
    }
}
