using System.Reflection;
using System.Security.Cryptography;
using AchievementLabs.Desktop;
using AchievementLabs.Services.Xbox360;

static class Xbox360ProfilePreviewChecks
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Set(DesktopModel model, string property, object value) => typeof(DesktopModel).GetProperty(property, Flags)!.SetValue(model, value);
    static byte[] Hash(string path) { using var file=File.OpenRead(path); return SHA256.HashData(file); }
    public static void Run(string source, string output)
    {
        Directory.CreateDirectory(output);
        var originalHash = Hash(source);
        var packages = new Xbox360PackageService();
        Check(Xbox360ConSigner.VerifyFile(source), "Original profile signature/hash did not verify");
        var profiles = new Xbox360ProfileService(Path.Combine(Path.GetFullPath(output),"private-profile"));
        var working = profiles.ExtractProfileAsync(source).GetAwaiter().GetResult();
        Check(Hash(working).SequenceEqual(originalHash), "Extracted copy differs from source");
        var reader = new Xbox360ProfileReader(packages);
        var games = reader.ReadGames(working);
        var sample = games.Select(game => { try { return (game, rows:reader.ReadAchievements(working, game.TitleId)); } catch (FileNotFoundException) { return (game, rows:(IReadOnlyList<AchievementLabs.Models.Xbox360ProfileAchievement>)[]); } })
            .FirstOrDefault(x=>x.rows.Any(a=>(a.Flags&0x20000)==0));
        Check(sample.game != null, "No game with locked achievements was found");
        using var model = new DesktopModel();
        typeof(DesktopModel).GetField("profiles", Flags)!.SetValue(model, profiles);
        model.PackagePath = working;
        model.LoadXbox360ProfileAsync().GetAwaiter().GetResult();
        Set(model,"Xbox360EditorTitleId", sample.game!.TitleId);
        Set(model,"Xbox360Achievements",sample.rows.ToArray());
        model.SelectXbox360PreviewAchievements(true);
        model.Xbox360UnlockMode="Unlocked Online";
        model.Xbox360PreviewStart="2021-01-01T00:00:00Z";
        model.PreviewXbox360TimestampsAsync().GetAwaiter().GetResult();
        Check(model.Xbox360PreviewRows.Length>0, "Preview failed: "+model.Xbox360PreviewStatus);
        Check(model.Xbox360PreviewRows.Any(x=>x.Result=="Ready for review"), "No unambiguous dates previewed");
        var reviewCount=model.Xbox360PreviewRows.Length;
        var importPath=Path.Combine(output,"private-reference.json");
        File.WriteAllText(importPath,System.Text.Json.JsonSerializer.Serialize(new { list=sample.rows.Where(a=>(a.Flags&0x20000)==0).Select(a=>new {name=a.Name,timestamp=1609459200}) }));
        model.PreviewXbox360TimestampsAsync(importPath).GetAwaiter().GetResult();
        Check(model.Xbox360PreviewRows.Length==0 && model.Xbox360PreviewStatus.Contains("Confirm"), "Reference edition confirmation must be explicit");
        model.Xbox360ReferenceEditionConfirmed=true;
        model.PreviewXbox360TimestampsAsync(importPath).GetAwaiter().GetResult();
        Check(model.Xbox360PreviewRows.Length==reviewCount && model.Xbox360PreviewRows.Any(x=>x.Result=="Ready for review"), "Confirmed import preview failed");
        Check(Hash(working).SequenceEqual(originalHash), "Preview changed the working copy");
        model.PackagePath=source;
        model.PreviewXbox360TimestampsAsync().GetAwaiter().GetResult();
        Check(model.Xbox360PreviewRows.Length==0 && model.Xbox360PreviewStatus.StartsWith("Preview blocked:"), "Switching to the original must block preview");
        model.PackagePath=working;
        // Alter a certificate byte in the disposable copy, never the original or its verified backup.
        using(var file=File.Open(working,FileMode.Open,FileAccess.ReadWrite)){file.Position=0x10;var b=file.ReadByte();file.Position=0x10;file.WriteByte((byte)(b^1));}
        model.PreviewXbox360TimestampsAsync().GetAwaiter().GetResult();
        Check(model.Xbox360PreviewRows.Length==0 && model.Xbox360PreviewStatus.StartsWith("Preview blocked:"), "Changed source snapshot must block preview");
        Check(Hash(source).SequenceEqual(originalHash), "Original profile changed");
        var report=$"PASS: supplied profile signature and metadata; verified working copy/backup; {games.Count} dashboard games; {reviewCount} selected rows previewed; original-path and changed-copy guards; original bytes unchanged. No signing, USB injection, account calls or uploads. Test copy intentionally altered to verify rejection; retain the .bak as the untouched copy.";
        File.WriteAllText(Path.Combine(output,"profile-validation.txt"),report);
        Console.WriteLine(report);
    }
}
