using System.Net;
using System.Reflection;
using System.Text.Json;
using AchievementLabs.Core;
using AchievementLabs.Desktop;
using AchievementLabs.Desktop.Workflows;
using AchievementLabs.Models;
using AchievementLabs.Services.Xbox360;

static class Windows8ReferenceTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class ExophaseHandler(string gameName = "Example Windows 8") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Get, "Reference lookup attempted a write.");
            var uri = request.RequestUri!;
            var body = uri.Host == "www.exophase.com" ? "<script>playerProfileId = 'fixture'</script>"
                : uri.AbsolutePath.EndsWith("/earned", StringComparison.Ordinal)
                    ? """{"list":[{"name":"First","timestamp":1704200000}]}"""
                : uri.Query.Contains("page=1", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { success = true, games = new[]
                    {
                        new { master_id = 42, meta = new { title = gameName, platforms = new[] { new { slug = "windows8" } } } },
                        new { master_id = 43, meta = new { title = "Xbox 360 edition", platforms = new[] { new { slug = "xbox360" } } } }
                    } })
                    : """{"success":true,"games":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    public static void Run()
    {
        var matcher = typeof(DesktopModel).Assembly.GetType("AchievementLabs.Desktop.Windows8ReferenceDates")!
            .GetMethod("Match", BindingFlags.NonPublic | BindingFlags.Static)!;
        AchievementsResponse Match(AchievementsResponse catalog, IReadOnlyList<Xbox360ReferenceUnlock> dates,
            IReadOnlyList<ExophaseAwardIdentity>? identities = null) =>
            (AchievementsResponse)matcher.Invoke(null, [catalog, dates, identities])!;
        var stamp = new DateTimeOffset(2024, 1, 2, 20, 4, 5, 123, TimeSpan.Zero);
        var catalog = new AchievementsResponse { achievements =
        [
            new OneCoreAchievementResponse { id = "7", name = "First", gamerscore = 10 },
            new OneCoreAchievementResponse { id = "8", name = "Second", gamerscore = 20 }
        ] };
        var direct = Match(catalog, [new Xbox360ReferenceUnlock("First", stamp)]);
        Check(direct.achievements.Single().id == "7" && direct.achievements.Single().progression?.timeUnlocked ==
              "2024-01-02T20:04:05.1230000Z", "Imported date did not match the Windows 8 catalog ID or precision.");
        var localized = new AchievementsResponse { achievements =
        [
            new OneCoreAchievementResponse { id = "7", name = "Localized one", gamerscore = 10 },
            new OneCoreAchievementResponse { id = "8", name = "Localized two", gamerscore = 20 }
        ] };
        var fallback = Match(localized,
            [new Xbox360ReferenceUnlock("First", stamp), new Xbox360ReferenceUnlock("Second", stamp.AddMinutes(1))],
            [new ExophaseAwardIdentity("7", "First", 10), new ExophaseAwardIdentity("8", "Second", 20)]);
        Check(fallback.achievements.Count == 2 && fallback.achievements[0].id == "7" && fallback.achievements[0].name == "Localized one",
            "Verified award IDs did not recover localized catalog names.");
        try
        {
            Match(localized, [new Xbox360ReferenceUnlock("First", stamp), new Xbox360ReferenceUnlock("Second", stamp)],
                [new ExophaseAwardIdentity("7", "First", 99), new ExophaseAwardIdentity("8", "Second", 20)]);
            throw new Exception("Mismatched award points were accepted.");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { }
        var html = """<li class="earned" data-award-id="7" data-points="10" data-earned="1704200000"><div class="award-title"><a>First</a></div></li>""";
        Check(Xbox360TimestampPreview.ParseAwardIdentities(html).Single() == new ExophaseAwardIdentity("7", "First", 10),
            "Saved page award identity was not parsed.");
        using var exophase = new Exophase360Client(new ExophaseHandler());
        var games = exophase.FindWindows8GamesAsync("Fixture", true).GetAwaiter().GetResult();
        Check(games.Count == 1 && games[0].Id == 42, "Public lookup mixed Windows 8 with Xbox 360 editions.");
        Check(exophase.LoadDatesAsync("Fixture", true, 42).GetAwaiter().GetResult().Count == 1,
            "Public lookup did not load earned dates.");
        var path = Path.Combine(Path.GetTempPath(), "AchievementLabs-OfflineChecks", Environment.ProcessId.ToString(),
            "win8-reference-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"list":[{"name":"First","timestamp":1704200000}]}""");
        var runner = new AutoUnlockerViewModel(new NativeNotices(_ => { }), new NativeAccountContext())
        {
            ReferenceGamertag = "Fixture", TitleId = "1297292157", Windows8ReferenceEditionConfirmed = true,
            ReadWindows8CatalogAsync = (_, _) => Task.FromResult<AchievementsResponse?>(catalog)
        };
        runner.ImportWindows8ReferenceAsync(path).GetAwaiter().GetResult();
        var read = typeof(AutoUnlockerViewModel).GetMethod("ReadWindows8ReferenceForQueueAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var imported = (Task<AchievementsResponse>)read.Invoke(runner,
            ["456", "Fixture", "1297292157", CancellationToken.None])!;
        Check(imported.GetAwaiter().GetResult().achievements.Single().id == "7",
            "Auto Unlock did not use the imported Windows 8 reference dates.");
        runner.TitleId = "1297292136";
        try
        {
            ((Task<AchievementsResponse>)read.Invoke(runner, ["456", "Fixture", "1297292136", CancellationToken.None])!)
                .GetAwaiter().GetResult();
            throw new Exception("Imported dates were reused for another Windows 8 title.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("another reference player or title")) { }
        var publicTitle = XboxTitleCatalog.Find("1297292157")!.Name;
        var fallbackRunner = new AutoUnlockerViewModel(new NativeNotices(_ => { }), new NativeAccountContext())
        {
            ReferenceGamertag = "Fixture", TitleId = "1297292157",
            ReadWindows8ReferenceAsync = (_, _, _) => Task.FromException<AchievementsResponse?>(
                new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden)),
            ReadWindows8CatalogAsync = (_, _) => Task.FromResult<AchievementsResponse?>(catalog),
            CreateWindows8PublicClient = () => new Exophase360Client(new ExophaseHandler(publicTitle))
        };
        var publicTask = (Task<AchievementsResponse>)read.Invoke(fallbackRunner,
            ["456", "Fixture", "1297292157", CancellationToken.None])!;
        Check(publicTask.GetAwaiter().GetResult().achievements.Single().id == "7" &&
              fallbackRunner.ReferenceInfo.Contains("Exophase dates"),
            "Xbox 403 did not fall back to the exact public Windows 8 reference edition.");
        Console.WriteLine("PASS: Windows 8 reference dates, edition filter, catalog matching, and saved-page fallback. Mock HTTP only.");
    }
}
