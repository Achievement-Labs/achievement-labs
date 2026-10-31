using AchievementLabs.Desktop.Platforms;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AchievementLabs.Desktop;

public partial class MainWindow
{
    private async Task CheckPlatformUiAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var output = args.SkipWhile(a => a != "--output").Skip(1).First();
        var fixtures = args.SkipWhile(a => a != "--fixtures").Skip(1).First();
        Directory.CreateDirectory(output);
        try
        {
            await AccountLibraryChecks.RunAsync(output);
            await PlatformAchievementChecks.RunAsync(output);
            await model.CheckPlatformAchievementLoadingAsync();
            var database = Path.Combine(fixtures, "galaxy-fixture.db");
            var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database));
            var undecided = GogCatalogReader.Read(database, "", default);
            if (undecided.Accounts.Length != 2 || undecided.Games.Single(g => g.Id == "123").Achievements.Any(a => a.Unlocked.HasValue)) throw new Exception("Galaxy multiple accounts were merged or implicitly selected");
            var first = GogCatalogReader.Read(database, "101", default).Games.Single(g => g.Id == "123");
            var second = GogCatalogReader.Read(database, "202", default).Games.Single(g => g.Id == "123");
            if (first.Achievements.Single(a => a.Id == "FIRST").Unlocked != true || second.Achievements.Single(a => a.Id == "FIRST").Unlocked != false || first.ProgressKnown || first.Achievements.Single(a => a.Id == "UNKNOWN").Unlocked != null) throw new Exception("Galaxy account isolation or missing states failed");
            using (var readOnly = new ReadOnlySqlite(database))
            {
                var rejected = false;
                try { readOnly.Query("UPDATE Products SET name='must not write'"); } catch (IOException) { rejected = true; }
                if (!rejected) throw new Exception("SQLite cache reader allowed writes");
            }
            if (!before.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(database)))) throw new Exception("Galaxy fixture database changed");
            var walDatabase = Path.Combine(fixtures, "galaxy-wal-fixture.db");
            if (File.Exists(walDatabase))
            {
                string temporary;
                using (var copy = new GalaxyCacheCopy(walDatabase, default))
                {
                    temporary = Path.GetDirectoryName(copy.DatabasePath)!;
                    var result = GogCatalogReader.Read(copy.DatabasePath, "101", default);
                    if (!result.Games.Any(g => g.Id == "456")) throw new Exception("Uncheckpointed Galaxy WAL data was lost");
                }
                if (Directory.Exists(temporary)) throw new Exception("Temporary Galaxy cache was not cleaned up");
            }
            var imported = Path.Combine(output, "imported-fixtures");
            var ea = Path.Combine(fixtures, "EA.json");
            PlatformSnapshot.Import(ea, "EA", imported);
            PlatformSnapshot.Import(ea, "EA", imported);
            if (Directory.GetFiles(imported, "*.bak").Length == 0 || PlatformSnapshot.Apply("EA", [], imported).Games.Single().Total != 3) throw new Exception("Snapshot import or backup");
            var wrongPlatformRejected = false;
            try { PlatformSnapshot.Import(ea, "GOG", imported); } catch (InvalidDataException) { wrongPlatformRejected = true; }
            if (!wrongPlatformRejected) throw new Exception("Cross-platform snapshot accepted");
            var invalid = File.ReadAllText(ea).Replace("offline-fixture", "");
            var invalidRejected = false;
            try { PlatformSnapshot.Parse(invalid); } catch (InvalidDataException) { invalidRejected = true; }
            if (!invalidRejected) throw new Exception("Player snapshot accepted without account identity");
            var root = this.FindControl<Grid>("PreviewRoot")!;
            foreach (var platform in new[] { "EA", "GOG", "Epic", "Ubisoft" })
            {
                model.LoadPlatformPreview(platform, fixtures);
                model.CheckPlatformPresentation();
                var selectedGame = model.CurrentPlatformGame?.Id;
                await model.OpenPlatformAsync(platform, true);
                if (!model.PlatformAchievementsVisible || model.CurrentPlatformGame?.Id != selectedGame) throw new Exception("Sidebar achievement navigation lost the selected game");
                model.AddPlatformLayoutFixtures();
                foreach (var width in new[] { 1440, 1120, 1000, 900 })
                {
                    Width = width; Height = width == 1440 ? 920 : width == 900 ? 650 : 720;
                    foreach (var view in new[] { "library", "achievements" })
                    {
                        if (view == "library") model.ShowPlatformLibrary(); else { model.ShowPlatformAchievements(); model.CurrentPlatformAchievement = model.PlatformAchievementRows.FirstOrDefault(); }
                        await Task.Delay(200);
                        if (view == "library")
                        {
                            var list = this.FindControl<ListBox>("PlatformGameList")!;
                            foreach (var button in list.GetVisualDescendants().OfType<Button>())
                            {
                                var origin = button.TranslatePoint(new Point(), list)!.Value;
                                if (origin.X < 0 || origin.X + button.Bounds.Width > list.Bounds.Width) throw new Exception("Platform library button clipped inside its list viewport");
                            }
                            foreach (var row in list.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("compact-library-row")))
                                if (row.Bounds.Width > list.Bounds.Width) throw new Exception("Platform library columns overflowed their list viewport");
                        }
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height), new Vector(96, 96));
                        bitmap.Render(root); bitmap.Save(Path.Combine(output, $"{platform}-{view}-{width}.png"), new PngBitmapEncoderOptions());
                    }
                }
            }
            // Smoke-read local metadata only; never launches a client or performs an account operation.
            var local = new List<string>();
            foreach (var platform in new[] { "EA", "GOG", "Epic", "Ubisoft" })
            {
                try
                {
                    var result = await Task.Run(() => PlatformLibraryReader.Read(platform, "", new(), new(), default));
                    local.Add($"{platform}: {result.Games.Length} local titles, {result.Games.Sum(g => g.Total)} cached achievement rows");
                }
                catch (Exception error) { local.Add($"{platform}: unavailable ({error.GetType().Name}: {error.Message})"); }
            }
            File.WriteAllLines(Path.Combine(output, "local-read-summary.txt"), local);
            File.WriteAllText(Path.Combine(output, "platform-checks.txt"), "PASS: read-only SQLite and Galaxy account isolation; snapshot validation; four-platform completion/filter/sort/search; separate library/achievement navigation retains selection; long-title scrolling lists and action bounds at 1440/1120/1000/900. No unlocks, client launches, spoofing or launcher database writes.");
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "platform-error.txt"), error.ToString()); Environment.ExitCode = 1; }
        Close();
    }
}
