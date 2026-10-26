using System.Reflection;
using System.Security.Cryptography;
using AchievementLabs.Desktop;
using AchievementLabs.Services.Xbox360;

static class Xbox360SigningChecks
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static byte[] Hash(string path) { using var file=File.OpenRead(path); return SHA256.HashData(file); }
    public static void Run(string source, string horizon, string output)
    {
        Directory.CreateDirectory(output);
        var originalHash=Hash(source);
        Xbox360SigningSession.Clear();
        if (Xbox360SigningSession.HasBundledIdentity)
        {
            using var standalone = new DesktopModel { Xbox360SigningStorePath=Path.Combine(output,"no-custom-signing.bin") };
            standalone.RestoreXbox360SigningData();
            Check(Xbox360ConSigner.SigningAvailable,"Standalone signing must start without Horizon or a user cache");
            Console.WriteLine("PASS: bundled local-build signing identity loads with no Horizon import or saved user setup.");
        }
        Xbox360SigningSession.Clear();
        Check(!Xbox360ConSigner.SigningAvailable,"Signing must start disabled after clearing");
        Xbox360SigningSession.LoadHorizonFile(horizon);
        Check(Xbox360ConSigner.SigningAvailable,"Horizon material did not load");
        Console.WriteLine("PASS: static Horizon signing data import and certificate/key consistency; no executable code loaded.");
        var badKey=Path.Combine(output,"invalid-signing.bin"); File.WriteAllBytes(badKey,new byte[0x4000]);
        try { Xbox360SigningSession.LoadKeyvault(badKey); throw new Exception("Invalid keyvault accepted"); } catch(InvalidDataException) { }
        Check(Xbox360ConSigner.SigningAvailable,"Invalid import must preserve the previous valid session");
        var remembered=Path.Combine(output,"private-signing.bin");
        Xbox360SigningSession.Remember(remembered); Xbox360SigningSession.Clear();
        Xbox360SigningSession.Restore(remembered);
        Check(Xbox360ConSigner.SigningAvailable,"Encrypted signing restore failed");
        var corrupt=Path.Combine(output,"invalid-saved-signing.bin"); File.WriteAllBytes(corrupt,new byte[100]);
        try { Xbox360SigningSession.Restore(corrupt); throw new Exception("Corrupt signing store accepted"); } catch(CryptographicException) { }
        Check(Xbox360ConSigner.SigningAvailable,"Failed restore must retain current valid signing data");
        Console.WriteLine("PASS: Windows-user-encrypted signing setup restores without reading Horizon; corrupt saved data rejected.");
        if (Xbox360SigningSession.HasBundledIdentity)
        {
            Xbox360SigningSession.Clear(); Xbox360SigningSession.LoadBundledIdentity();
            Console.WriteLine("PASS: subsequent profile-save checks use the bundled identity, without reading Horizon during signing.");
        }
        var packages=new Xbox360PackageService();
        var profiles=new Xbox360ProfileService(Path.Combine(Path.GetFullPath(output),"private-profile"));
        var working=profiles.ExtractProfileAsync(source).GetAwaiter().GetResult();
        Xbox360StfsIntegrity.VerifyFile(working);
        var reader=new Xbox360ProfileReader(packages);
        var sample=reader.ReadGames(working).Select(g=> {
            try {return (game:g,rows:reader.ReadAchievements(working,g.TitleId));}
            catch(FileNotFoundException) {return (game:g,rows:(IReadOnlyList<AchievementLabs.Models.Xbox360ProfileAchievement>)[]);}
        }).First(x=>x.rows.Count(a=>(a.Flags&0x20000)==0)>=8);
        using var model=new DesktopModel();
        typeof(DesktopModel).GetField("profiles",Flags)!.SetValue(model,profiles);
        model.PackagePath=working; model.LoadXbox360ProfileAsync().GetAwaiter().GetResult();
        typeof(DesktopModel).GetProperty("Xbox360EditorTitleId",Flags)!.SetValue(model,sample.game.TitleId);
        typeof(DesktopModel).GetProperty("Xbox360Achievements",Flags)!.SetValue(model,sample.rows.ToArray());
        Check(model.HasValidatedXbox360WorkingCopy && model.CanModifyXbox360Profile,"A verified working profile should enable editing without an acknowledgement checkbox");

        foreach(var choice in model.Xbox360Choices.Where(c=>(c.Achievement.Flags&0x20000)==0).Take(2)) choice.Selected=true;
        model.Xbox360UnlockMode="Unlocked Online"; model.Xbox360PreviewStart="2021-01-01T00:00:00Z"; model.Xbox360PreviewStepMinutes=2;
        model.PreviewXbox360TimestampsAsync().GetAwaiter().GetResult();
        Check(model.CanStageXbox360Preview,"Preview failed: "+model.Xbox360PreviewStatus);
        var expected=model.Xbox360PreviewRows.Where(r=>r.Result=="Ready for review").Select(r=>uint.Parse(r.AchievementId)).ToArray();
        Check(expected.Length==2,"Expected two reviewable achievements");
        model.StageXbox360TimestampPreview();
        Check(model.PendingXbox360EditCount==2,"Staging failed: "+model.Xbox360PreviewStatus);
        Check(Hash(working).SequenceEqual(originalHash),"Staging must not write to disk");
        model.SelectedLocalXbox360Profile=source;
        model.LoadSelectedXbox360ProfileAsync().GetAwaiter().GetResult();
        Check(model.PackagePath==working && model.PendingXbox360EditCount==2,"Switching profiles must not silently discard pending edits");
        model.SaveXbox360EditsAsync().GetAwaiter().GetResult();
        Check(model.PendingXbox360EditCount==0,"Save failed: "+model.LegacyOutput);
        Check(Xbox360ConSigner.VerifyFile(working),"Signed saved profile failed verification");
        Xbox360StfsIntegrity.VerifyFile(working);
        var written=reader.ReadAchievements(working,sample.game.TitleId);
        Check(expected.All(id=>written.Single(a=>a.Id==id).Flags is var flags && (flags & 0x30000u)==0x30000u),"Selected unlock flags not saved");
        Check(written.Where(a=>!expected.Contains(a.Id)).SequenceEqual(sample.rows.Where(a=>!expected.Contains(a.Id))),"Unselected achievement data changed");
        var dashboard=reader.ReadGames(working).Single(g=>g.TitleId==sample.game.TitleId);
        Check(dashboard.UnlockedCount==sample.game.UnlockedCount+2,"Dashboard achievement total mismatch");
        Check(dashboard.GamerscoreUnlocked==sample.game.GamerscoreUnlocked+sample.rows.Where(a=>expected.Contains(a.Id)).Sum(a=>(long)a.Gamerscore),"Dashboard gamerscore mismatch");
        Check(Directory.GetFiles(Path.GetDirectoryName(working)!,"*.before-save-*.bak").Any(p=>Hash(p).SequenceEqual(originalHash)),"Pre-save backup mismatch");
        Check(Hash(working+".bak").SequenceEqual(originalHash),"Extraction backup changed");
        Check(Hash(source).SequenceEqual(originalHash),"Original exported profile changed");
        Console.WriteLine("PASS: two selected achievements staged without disk writes, then signed working-copy save; readback, dashboard totals, unchanged unselected achievements and verified backups; original unchanged. No USB or console sync attempted.");
        var savedHash=Hash(working);
        // A second save exercises offline/undated flags and continued use after a verified save.
        var offline=model.Xbox360Choices.First(c=>(c.Achievement.Flags&0x20000)==0);
        offline.Selected=true; model.Xbox360UnlockMode="Unlocked Offline";
        model.PreviewXbox360TimestampsAsync().GetAwaiter().GetResult(); model.StageXbox360TimestampPreview();
        Check(model.PendingXbox360EditCount==1,"Offline staging failed");
        model.SaveXbox360EditsAsync().GetAwaiter().GetResult();
        var offlineWritten=reader.ReadAchievements(working,sample.game.TitleId).Single(a=>a.Id==offline.Achievement.Id);
        Check(model.PendingXbox360EditCount==0 && (offlineWritten.Flags&0x30000u)==0x20000u && offlineWritten.UnlockedAt==null,"Offline signed save failed");
        Xbox360StfsIntegrity.VerifyFile(working);
        savedHash=Hash(working);
        Console.WriteLine("PASS: subsequent offline/undated save and full active STFS hash-tree verification.");
        model.Xbox360SigningStorePath=remembered;
        Check(!model.UnlockSelectedXbox360Async().GetAwaiter().GetResult() && Hash(working).SequenceEqual(savedHash),"Empty quick-unlock selection must not write");
        var quick=model.Xbox360Choices.First(c=>(c.Achievement.Flags&0x20000)==0); quick.Selected=true;
        model.Xbox360QuickUnlockMode="Custom dates"; model.Xbox360PreviewStart="2099-01-01T00:00:00Z";
        Check(!model.UnlockSelectedXbox360Async().GetAwaiter().GetResult() && Hash(working).SequenceEqual(savedHash),"Future quick-unlock dates must not write");
        model.Xbox360QuickUnlockMode="Offline (no date)";
        var firstClick=model.UnlockSelectedXbox360Async(); var secondClick=model.UnlockSelectedXbox360Async();
        Check(firstClick.GetAwaiter().GetResult() && !secondClick.GetAwaiter().GetResult(),"One-click save or double-click guard failed: "+model.Xbox360UnlockStatus);
        var quickRead=reader.ReadAchievements(working,sample.game.TitleId).Single(a=>a.Id==quick.Achievement.Id);
        Check((quickRead.Flags&0x30000u)==0x20000u && quickRead.UnlockedAt==null,"One-click offline unlock failed");
        quick=model.Xbox360Choices.First(c=>(c.Achievement.Flags&0x20000)==0); quick.Selected=true; model.Xbox360QuickUnlockMode="Online (now)";
        Check(model.UnlockSelectedXbox360Async().GetAwaiter().GetResult(),"One-click online unlock failed: "+model.Xbox360UnlockStatus);
        quickRead=reader.ReadAchievements(working,sample.game.TitleId).Single(a=>a.Id==quick.Achievement.Id);
        Check((quickRead.Flags&0x30000u)==0x30000u && quickRead.UnlockedAt.HasValue,"One-click online flags/date failed");
        var custom=model.Xbox360Choices.Where(c=>(c.Achievement.Flags&0x20000)==0).Take(2).ToArray(); foreach(var c in custom) c.Selected=true;
        model.Xbox360QuickUnlockMode="Custom dates"; model.Xbox360PreviewStart="2021-01-01T00:00:00Z"; model.Xbox360PreviewStepMinutes=2;
        Check(model.UnlockSelectedXbox360Async().GetAwaiter().GetResult(),"One-click custom dates failed: "+model.Xbox360UnlockStatus);
        var customRead=reader.ReadAchievements(working,sample.game.TitleId).Where(a=>custom.Any(c=>c.Achievement.Id==a.Id)).OrderBy(a=>a.Id).ToArray();
        Check(customRead[0].UnlockedAt==DateTimeOffset.Parse("2021-01-01T00:00:00Z") && customRead[1].UnlockedAt==DateTimeOffset.Parse("2021-01-01T00:02:00Z"),"Custom timestamp spacing mismatch");
        var imported=model.Xbox360Choices.First(c=>(c.Achievement.Flags&0x20000)==0 && model.Xbox360Choices.Count(x=>x.Name==c.Name)==1); imported.Selected=true;
        var reference=Path.Combine(output,"private-reference-simple.json"); File.WriteAllText(reference,System.Text.Json.JsonSerializer.Serialize(new { list=new[]{new {name=imported.Name,timestamp=1609459200}} }));
        model.Xbox360ReferenceEditionConfirmed=true; model.PreviewXbox360TimestampsAsync(reference).GetAwaiter().GetResult(); model.Xbox360QuickUnlockMode="Imported dates";
        Check(model.UnlockSelectedXbox360Async().GetAwaiter().GetResult(),"One-click imported dates failed: "+model.Xbox360UnlockStatus);
        Check(reader.ReadAchievements(working,sample.game.TitleId).Single(a=>a.Id==imported.Achievement.Id).UnlockedAt==DateTimeOffset.Parse("2021-01-01T00:00:00Z"),"Imported date mismatch");
        Xbox360StfsIntegrity.VerifyFile(working);
        Check(Hash(source).SequenceEqual(originalHash),"One-click operations changed original profile");
        savedHash=Hash(working);
        model.ClearXbox360SigningData(); Check(!File.Exists(remembered) && Xbox360ConSigner.SigningAvailable==Xbox360SigningSession.HasBundledIdentity,"Forget must remove custom setup and restore the standalone default when present");
        Xbox360SigningSession.Clear();
        Check(!model.UnlockSelectedXbox360Async().GetAwaiter().GetResult() && Hash(working).SequenceEqual(savedHash),"Unlock without signing must not write");
        Console.WriteLine("PASS: single-action offline/online save-and-resign; empty/future selection and double-click guards; forget signing setup removes encrypted identity; original unchanged.");
        var editor=new Xbox360ProfileEditService(packages,profiles);
        try { editor.Save(working,sample.game.TitleId,[written[0] with { Gamerscore=written[0].Gamerscore+1, Flags=written[0].Flags^0x100000u }]); throw new Exception("Changed metadata accepted"); } catch(InvalidDataException) { }
        Check(Hash(working).SequenceEqual(savedHash),"Rejected edit changed working copy");
        var referenceChoice=model.Xbox360Choices.First(c=>(c.Achievement.Flags&0x20000)==0 && model.Xbox360Choices.Count(x=>x.Name==c.Name)==1); referenceChoice.Selected=true;
        typeof(DesktopModel).GetProperty("Xbox360EditorTitleName",Flags)!.SetValue(model,sample.game.Name);
        model.Xbox360ExophaseKind="Exophase profile"; model.Xbox360ExophaseName="fixture"; model.Xbox360ExophaseClientFactory=()=>Exophase360Tests.Fixture(sample.game.Name,referenceChoice.Name);
        model.LoadXbox360ExophaseDatesAsync().GetAwaiter().GetResult();
        Check(model.Xbox360QuickUnlockMode=="Imported dates" && model.Xbox360PreviewRows.Count(r=>r.Result=="Ready for review")==1,"Profile-name lookup did not prepare dates: "+model.Xbox360PreviewStatus);
        Check(Hash(working).SequenceEqual(savedHash),"Reference lookup must not write the profile");
        model.Xbox360ExophaseName="differentfixture"; Check(model.Xbox360PreviewRows.Length==0,"Changing the reference profile must clear old dates");
        model.Xbox360ExophaseClientFactory=()=>Exophase360Tests.Fixture(sample.game.Name,referenceChoice.Name,true);
        model.LoadXbox360ExophaseDatesAsync().GetAwaiter().GetResult();
        Check(model.Xbox360PreviewRows.Length==0 && model.Xbox360PreviewStatus.Contains("blocked") && Hash(working).SequenceEqual(savedHash),"Blocked lookup must not leave actionable stale dates or write the profile");
        Console.WriteLine("PASS: profile-name reference lookup prepares selected dates without a file or profile write; changing names clears dates; blocked lookups fail safely. Fixture network responses.");
        model.Xbox360ExophaseKind="Xbox Live"; model.Xbox360ExophaseName="referencefixture";
        model.Xbox360ReferenceFetcher=(gt,numeric,token)=> {
            Check(gt=="referencefixture" && numeric==Convert.ToUInt32(sample.game.TitleId,16).ToString(),"Xbox lookup input must be a gamertag and decimal Title ID");
            return Task.FromResult<Xbox360AchievementResponse?>(new() {achievements=[new() {id=(int)referenceChoice.Achievement.Id,titleId=Convert.ToUInt32(sample.game.TitleId,16),gamerscore=(int)referenceChoice.Gamerscore,unlocked=true,unlockedOnline=true,timeUnlocked="2021-01-01T00:00:00Z",name="Localized reference name"}]});
        };
        model.LoadXbox360ExophaseDatesAsync().GetAwaiter().GetResult();
        Check(model.Xbox360PreviewRows.Count(r=>r.Result=="Ready for review")==1 && model.Xbox360PreviewStatus.Contains("from Xbox") && Hash(working).SequenceEqual(savedHash),"Xbox gamertag reference dates should load by ID without writing the profile: "+model.Xbox360PreviewStatus);
        Console.WriteLine("PASS: default Xbox gamertag lookup prepares 360 dates by title/achievement ID without contacting Exophase or writing the profile. Fixture Xbox response.");
        // A changed source must be rejected even when it still has a valid CON header signature.
        using(var file=File.Open(working,FileMode.Open,FileAccess.ReadWrite)) { file.Position=0xC000; var b=file.ReadByte();file.Position=0xC000;file.WriteByte((byte)(b^1)); }
        try { Xbox360StfsIntegrity.VerifyFile(working); throw new Exception("Damaged data accepted"); } catch(InvalidDataException) { }
        Check(Hash(source).SequenceEqual(originalHash),"Original profile changed after negative checks");
        Console.WriteLine("PASS: invalid key import retains session; pending edits prevent profile switches; altered metadata and damaged STFS data rejected. Negative-test working copy is intentionally damaged; backups are intact.");
        Xbox360SigningSession.Clear(); Check(!Xbox360ConSigner.SigningAvailable,"Signing data clear failed");
        Check(!model.CanModifyXbox360Profile,"Clearing signing data must disable edits");
    }
}
