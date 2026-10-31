using System.Net;
using System.Text;
using System.IO.Compression;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Desktop.Platforms;

internal static class PlatformAchievementChecks
{
    internal static async Task RunAsync(string output)
    {
        CheckUbisoftBundleAndStates(output);
        using var client = new AccountLibraryClient(new AchievementHandler());
        foreach (var platform in new[] { "EA", "Epic", "GOG" })
        {
            var account = new ConnectedPlatformAccount(platform, "fixture-account", "Fixture", "fixture-access", PersonaId: "fixture-persona");
            var game = new PlatformGameRow(platform, "fixture-game", "Fixture game", AccountId: account.AccountId, ServiceId: "fixture-sandbox");
            var result = await client.ReadAchievementsAsync(account, game, default);
            if (result.Rows.Length != 2 || result.Rows.Count(a => a.Unlocked == true) != 1 || result.Rows.Count(a => a.Unlocked == false) != 1 || !result.Complete) throw new Exception(platform + " live reader mapping or paging failed");
            if (platform is "Epic" or "GOG" && result.Rows.Any(r => r.ImageUrl.Length == 0 || r.GlobalPercent == null)) throw new Exception(platform + " achievement icons/rarity lost");
            if (platform == "EA" && result.Rows.Any(r => r.GlobalPercent != null)) throw new Exception("EA missing rarity was fabricated");
            var mismatch = false;
            try { await client.ReadAchievementsAsync(account, game with { AccountId = "other-account" }, default); } catch (InvalidDataException) { mismatch = true; }
            if (!mismatch) throw new Exception("Cross-account achievement read accepted");
        }
        using var unavailable = new AccountLibraryClient(new AchievementHandler(playerUnavailable: true));
        var epic = await unavailable.ReadAchievementsAsync(new("Epic", "fixture-account", "Fixture", "fixture-access"), new("Epic", "fixture-game", "Fixture", AccountId: "fixture-account", ServiceId: "fixture-sandbox"), default);
        if (epic.Rows.Length != 2 || epic.Complete || epic.Rows.Any(a => a.Unlocked.HasValue)) throw new Exception("Unavailable Epic player states were displayed as locked");
        File.WriteAllText(Path.Combine(output, "achievement-reader-checks.txt"), "PASS: EA/Epic/GOG definitions and player-state reads, locked/unlocked mapping, GOG pagination, artwork/rarity, account isolation, unknown progress on player-state failure. Requests are read-only; fabricated sessions only.");
    }
    private static void CheckUbisoftBundleAndStates(string output)
    {
        using var bundle = new MemoryStream();
        using (var zip = new ZipArchive(bundle, ZipArchiveMode.Create, true))
        {
            using (var text = new StreamWriter(zip.CreateEntry("en-US_loc.txt").Open(), Encoding.Unicode)) text.Write("1\tFixture achievement\tFixture description\r\n");
            using (var definition = zip.CreateEntry("achievements.dat").Open()) definition.Write(Proto.Message(1, Proto.Join(Proto.Number(1, 1), Proto.Number(2, 1), Proto.Number(3, 1), Proto.Number(4, 1))));
            using (var icon = zip.CreateEntry("1.png").Open()) icon.Write(File.ReadAllBytes("src/AchievementLabs.App/Assets/achievement-labs-icon.png"));
            using (var invalid = zip.CreateEntry("../must-not-be-created.png").Open()) invalid.Write([1, 2, 3]);
        }
        var rows = UbisoftAchievementCatalog.ParseBundle(bundle.ToArray(), Path.Combine(output, "ubisoft-fixture-art"), default);
        if (rows.Length != 1 || rows[0].Name != "Fixture achievement" || rows[0].Description != "Fixture description" || rows[0].Unlocked != null || !File.Exists(new Uri(rows[0].ImageUrl).LocalPath) || File.Exists(Path.Combine(output, "must-not-be-created.png"))) throw new Exception("Ubisoft definition bundle parsing or image path isolation failed");
        var product = Proto.Join(Proto.Message(1, Encoding.UTF8.GetBytes("100")), Proto.Message(2, Encoding.UTF8.GetBytes("PC")));
        var earned = Proto.Message(1, Proto.Join(Proto.Number(1, 1), Proto.Number(2, 1700000000)));
        var state = Proto.Join(Proto.Message(1, Encoding.UTF8.GetBytes("fixture-account")), Proto.Message(2, Proto.Message(1, Proto.Join(Proto.Message(1, product), Proto.Message(2, earned)))));
        if (!UbisoftOwnershipClient.ParseAchievementStates(state, "fixture-account")["100"].ContainsKey("1")) throw new Exception("Ubisoft earned-state parsing failed");
        var mismatch = false;
        try { UbisoftOwnershipClient.ParseAchievementStates(state, "other-account"); } catch (InvalidDataException) { mismatch = true; }
        if (!mismatch) throw new Exception("Ubisoft player states accepted for another account");
    }
    private sealed class AchievementHandler(bool playerUnavailable = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Post) throw new Exception("Achievement reader attempted a write");
            JToken response;
            if (request.RequestUri!.Host == "gameplay.gog.com")
            {
                var second = request.RequestUri.Query.Contains("page_token=");
                response = new JObject { ["total_count"] = 2, ["page_token"] = second ? "" : "fixture-page-two", ["items"] = new JArray(new JObject { ["achievement_key"] = second ? "LOCKED" : "EARNED", ["name"] = second ? "Locked" : "Earned", ["description"] = "Fixture description", ["date_unlocked"] = second ? null : "2026-10-20T00:00:00Z", ["rarity"] = second ? 2.5 : 80, ["image_url_unlocked"] = "https://images.gog.com/earned.png", ["image_url_locked"] = "https://images.gog.com/locked.png" }) };
            }
            else
            {
                var query = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var text = query["query"]!.ToString();
                if (text.Contains("mutation", StringComparison.OrdinalIgnoreCase)) throw new Exception("Achievement reader attempted a mutation");
                if (request.RequestUri.Host == "service-aggregation-layer.juno.ea.com")
                    response = new JObject { ["data"] = new JObject { ["achievements"] = new JArray(new JObject { ["id"] = "fixture-set", ["achievements"] = new JArray(new JObject { ["id"] = "EARNED", ["name"] = "Earned", ["description"] = "Fixture description", ["awardCount"] = 1, ["date"] = "2026-10-20T00:00:00Z" }, new JObject { ["id"] = "LOCKED", ["name"] = "Locked", ["description"] = "Fixture description", ["awardCount"] = 0 }) }) } };
                else if (text.Contains("productAchievementsRecordBySandbox"))
                    response = new JObject { ["data"] = new JObject { ["Achievement"] = new JObject { ["productAchievementsRecordBySandbox"] = new JObject { ["sandboxId"] = "fixture-sandbox", ["totalAchievements"] = 2, ["achievements"] = new JArray(new[] { "EARNED", "LOCKED" }.Select(id => new JObject { ["achievement"] = new JObject { ["name"] = id, ["lockedDisplayName"] = id, ["unlockedDisplayName"] = id, ["unlockedDescription"] = "Fixture description", ["lockedIconLink"] = "https://cdn.epicgames.com/locked.png", ["unlockedIconLink"] = "https://cdn.epicgames.com/earned.png", ["rarity"] = new JObject { ["percent"] = 2.5 } } })) } } } };
                else
                {
                    if (playerUnavailable) return new(HttpStatusCode.Unauthorized);
                    if (query["variables"]?["epicAccountId"]?.ToString() != "fixture-account") throw new Exception("Epic player query was not scoped to the connected account");
                    response = new JObject { ["data"] = new JObject { ["PlayerAchievement"] = new JObject { ["playerAchievementGameRecordsBySandbox"] = new JObject { ["records"] = new JArray(new JObject { ["playerAchievements"] = new JArray(new JObject { ["playerAchievement"] = new JObject { ["epicAccountId"] = "fixture-account", ["sandboxId"] = "fixture-sandbox", ["achievementName"] = "EARNED", ["unlocked"] = true, ["unlockDate"] = "2026-10-20T00:00:00Z" } }) }) } } } };
                }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToString(), Encoding.UTF8, "application/json") };
        }
    }
}
