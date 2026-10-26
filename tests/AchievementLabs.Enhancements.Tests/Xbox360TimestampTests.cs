using AchievementLabs.Models;
using AchievementLabs.Services.Xbox360;
using AchievementLabs.Desktop;

static class Xbox360TimestampTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (Exception e) when(e is InvalidDataException or InvalidOperationException or ArgumentOutOfRangeException or System.Text.Json.JsonException) { return; } throw new Exception("Invalid data accepted"); }
    public static void Run()
    {
        var source = new[] {
            new Xbox360ProfileAchievement(1,"First Steps","","",10,0,null),
            new Xbox360ProfileAchievement(2,"Offline Award","","",20,0,null),
            new Xbox360ProfileAchievement(3,"Finished","","",30,0x20000,null),
            new Xbox360ProfileAchievement(4,"Duplicate","","",5,0,null),
            new Xbox360ProfileAchievement(5,"Duplicate","","",5,0,null)
        };
        Check(new Xbox360AchievementChoice(source[2],()=>{}).Unlocked && new Xbox360AchievementChoice(source[2],()=>{}).RowBackground!="Transparent" && !new Xbox360AchievementChoice(source[0],()=>{}).Unlocked,"Unlocked rows have a green presentation independently of selection");
        var xbox = new[] {
            new Xbox360AchievementEntry {id=1,titleId=42,gamerscore=10,unlocked=true,unlockedOnline=true,timeUnlocked="2021-01-01T00:00:00Z",name="Different localized name"},
            new Xbox360AchievementEntry {id=2,titleId=42,gamerscore=20,unlocked=true,unlockedOnline=false},
            new Xbox360AchievementEntry {id=3,titleId=42,gamerscore=30,unlocked=true,unlockedOnline=true,timeUnlocked="2021-01-01T00:00:00Z"}
        };
        var xboxRows=Xbox360TimestampPreview.MatchXbox(source,xbox,new HashSet<uint>{1,2,3},42);
        Check(xboxRows[0].Result=="Ready for review" && xboxRows[0].TimeUtc=="2021-01-01 00:00:00 UTC" && xboxRows[1].ProposedState=="Unlocked Offline" && xboxRows[2].Result.Contains("already"),"Xbox title/achievement IDs match localized names and preserve offline dates");
        Reject(()=>Xbox360TimestampPreview.MatchXbox(source,xbox,new HashSet<uint>{1},43));
        Check(Xbox360TimestampPreview.MatchXbox(source,[xbox[0],xbox[0]],new HashSet<uint>{1},42).Single().Result.Contains("duplicate"),"Duplicate Xbox IDs skipped");
        xbox[0].timeUnlocked="2099-01-01T00:00:00Z";
        Check(Xbox360TimestampPreview.MatchXbox(source,xbox,new HashSet<uint>{1},42).Single().Result.Contains("invalid"),"Future Xbox dates skipped");
        xbox[0].gamerscore=999;
        Check(Xbox360TimestampPreview.MatchXbox(source,xbox,new HashSet<uint>{1},42).Single().Result.Contains("gamerscore"),"Mismatched Xbox metadata skipped");
        var reference = Xbox360TimestampPreview.Parse("""{"list":[{"masterAwardId":98765,"slug":"first-steps","timestamp":1609459200},{"name":"Offline Award","timestamp":0},{"name":"Finished","timestamp":1609459200},{"name":"Duplicate","timestamp":1609459200}]}""");
        var rows = Xbox360TimestampPreview.Match(source, reference, source.Select(a=>a.Id).ToHashSet());
        Check(rows[0].AchievementId=="1" && rows[0].ProposedState=="Unlocked Online" && rows[0].TimeUtc=="2021-01-01 00:00:00 UTC", "Source award ID must not be used as Xbox ID; UTC is preserved");
        Check(rows[1].ProposedState=="Unlocked Offline" && rows[1].TimeUtc=="—", "Offline source stays untimed");
        Check(rows[2].Result.Contains("already") && rows[3].Result.Contains("ambiguous") && rows[4].Result.Contains("ambiguous"), "Unlocked and ambiguous local entries skipped");
        var duplicates = Xbox360TimestampPreview.Match(source, [reference[0],reference[0]], new HashSet<uint>{1});
        Check(duplicates.Single().Result.Contains("ambiguous"), "Duplicate remote names never guessed");
        Check(Xbox360TimestampPreview.Match(source,reference,new HashSet<uint>{2}).Count==1, "Only explicit selection is previewed");
        Reject(()=>Xbox360TimestampPreview.Match(source,reference,new HashSet<uint>()));
        var html = """<li data-award-id='99' class='award earned' data-earned='1609459200'><div class='award-title'><a>First &amp; Last</a></div></li><li data-award-id='100' class='award'><div>Locked</div></li>""";
        Check(Xbox360TimestampPreview.Parse(html).Single().Name=="First & Last", "Saved HTML extracts earned names and decodes entities without running it");
        Reject(()=>Xbox360TimestampPreview.Parse("""{"list":[{"name":"A","timestamp":-1}]}"""));
        Reject(()=>Xbox360TimestampPreview.Parse("""{"list":[{"name":"A","timestamp":9999999999}]}"""));
        Reject(()=>Xbox360TimestampPreview.Parse("""{"list":[{"name":"A"}]}"""));
        Reject(()=>Xbox360TimestampPreview.Parse(new string('x',2*1024*1024+1)));
        using var model = new DesktopModel();
        typeof(DesktopModel).GetProperty("Xbox360Achievements",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic)!.SetValue(model,source);
        model.Xbox360Choices[0].Selected=true;
        model.Xbox360AchievementFilter="Locked only";
        Check(model.VisibleXbox360Choices.Length==4 && model.VisibleXbox360Choices.All(c=>(c.Achievement.Flags&0x20000)==0),"Locked-only filter excludes unlocked achievements");
        model.Xbox360AchievementSort="A–Z";
        Check(model.VisibleXbox360Choices.First().Name=="Duplicate","Alphabetical sorting");
        model.Xbox360AchievementSort="Z–A";
        Check(model.VisibleXbox360Choices.First().Name=="Offline Award","Reverse alphabetical sorting");
        model.Xbox360AchievementFilter="Unlocked only";
        Check(model.VisibleXbox360Choices.Single().Name=="Finished" && model.Xbox360SelectionCount==1,"Filtering preserves hidden checkbox selection");
        model.Xbox360AchievementFilter="All"; model.Xbox360AchievementSort="Achievement ID";
        Check(model.VisibleXbox360Choices.Select(c=>c.Achievement.Id).SequenceEqual(new uint[]{1,2,3,4,5}) && model.VisibleXbox360Choices[0].Selected,"ID sorting restores order without losing selection");
        model.PreviewXbox360TimestampsAsync().GetAwaiter().GetResult();
        Check(model.Xbox360PreviewStatus.StartsWith("Preview blocked:") && model.Xbox360PreviewRows.Length==0, "No-profile preview is blocked");
        Check(!model.CanModifyXbox360Profile && !model.CanSaveXbox360Edits, "Signing and writes remain disabled");
        Check(source[0].Flags==0 && source[0].UnlockedAt==null, "Preview does not mutate source achievements");
        Console.WriteLine("PASS: 360 JSON/HTML imports, UTC/offline dates, explicit selection, ambiguous matching, input bounds and no-profile/signing guards. No profile writes or network.");
    }
}
