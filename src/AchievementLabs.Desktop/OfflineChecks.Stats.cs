using AchievementLabs.Desktop.Workflows;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;

namespace AchievementLabs.Desktop;

internal static partial class OfflineChecks
{
    private static async Task CheckStatsEditorAsync(string root)
    {
        foreach (var (file, expected) in new[] { ("TitleBasedStats.txt", "Title"), ("EventBasedStats.txt", "Event") })
            foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "StatsCatalog", file)))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+)\s*-");
                if (match.Success && StatsEditorViewModel.GetCatalogStatMode(match.Groups[1].Value) != expected)
                    throw new Exception("Stat catalog classification differs from the shipped lists.");
            }

        if (StatsEditorViewModel.BuildStatValue("123", "String", "0").Type != JTokenType.String)
            throw new Exception("String stats were coerced to numbers.");
        foreach (var value in new[] { "abc", "1.2", "NaN", "Infinity" })
        {
            try { StatsEditorViewModel.BuildStatValue(value, "Integer", "0"); throw new Exception("Invalid numeric stat accepted."); }
            catch (FormatException) { }
        }

        var account = new NativeAccountContext { XAUTH = "XBL3.0 x=999;offline", XUIDOnly = "123", EventsDirectory = root };
        var wire = new StatTransport();
        var vm = new StatsEditorViewModel(new NativeNotices(_ => { }), account,
            auth => new XboxApiClient(auth, wire), allowRemoteCatalog: false) { TitleId = "309062877", DebugRootDirectory = Path.Combine(root, "stat-diagnostics") };
        await vm.LoadStats();
        var stat = vm.StatItems.Single(i => i.Name == "Kills");
        if (!stat.IsEditable || !stat.IsTitleBasedStat) throw new Exception("Supported title stat was not editable.");
        stat.NewValue = "7"; await vm.UpdateSingleStat(stat);
        if (wire.Writes != 1 || stat.EditStatus != "Verified" || stat.CurrentValue != "7" || stat.NewValue != "")
            throw new Exception($"Title stat acceptance/readback failed: writes={wire.Writes}, status={stat.EditStatus}, current={stat.CurrentValue}, new={stat.NewValue}, owner={stat.OwnerXuid}, title={stat.TitleId}, vm={vm.StatusText}.");
        if (wire.LastWrite?["stats"]?["title"]?["Kills"]?["value"]?.ToString() != "7"
            || wire.LastWrite?["previousRevision"]?.Value<long>() != wire.PreviousRevision
            || wire.LastWrite?["revision"]?.Value<long>() <= wire.PreviousRevision)
            throw new Exception("Stat wire payload or revision handling was wrong.");

        var unwritableDebug = Path.Combine(root, "debug-path-is-a-file"); File.WriteAllText(unwritableDebug, "fixture");
        vm.DebugRootDirectory = unwritableDebug;
        wire.Propagate = false; stat.NewValue = "8"; await vm.UpdateSingleStat(stat);
        if (stat.EditStatus != "Accepted; refresh to verify" || stat.CurrentValue != "7")
            throw new Exception("Accepted but unobserved write was presented as verified.");
        vm.DebugRootDirectory = Path.Combine(root, "stat-diagnostics");
        stat.NewValue = "invalid"; var before = wire.Writes; await vm.UpdateSingleStat(stat);
        if (wire.Writes != before || stat.EditStatus != "Invalid value") throw new Exception("Invalid input was sent.");

        wire.WriteStatus = 403; stat.NewValue = "9"; await vm.UpdateSingleStat(stat);
        if (stat.IsEditable || stat.EditStatus != "Not Supported") throw new Exception("Unwritable stat was not labeled Not Supported.");

        wire.TitleId = "1"; vm.TitleId = "1"; await vm.LoadStats();
        if (vm.StatItems.Count == 0 || vm.StatItems.Any(i => i.IsEditable) || vm.StatItems.Any(i => i.EditStatus != "Not Supported"))
            throw new Exception("An unlisted title acquired write support from SCID discovery.");
        vm.TitleId = "322252289"; var requests = wire.Requests; await vm.LoadStats();
        if (wire.Requests != requests || !vm.StatusText.Contains("Not Supported")) throw new Exception("Event-only title was routed to title stats.");

        wire.TitleId = "309062877"; vm.TitleId = "309062877"; wire.WriteStatus = 204; await vm.LoadStats();
        stat = vm.StatItems.Single(i => i.Name == "Kills"); stat.NewValue = "10";
        vm.TitleId = "1611397130"; before = wire.Writes; await vm.UpdateSingleStat(stat);
        if (wire.Writes != before || stat.EditStatus != "Reload required") throw new Exception("A stale title row retained write access.");

        vm.TitleId = "309062877"; await vm.LoadStats(); stat = vm.StatItems.Single(i => i.Name == "Kills"); stat.NewValue = "11";
        account.XUIDOnly = "different"; before = wire.Writes; await vm.UpdateSingleStat(stat);
        if (wire.Writes != before) throw new Exception("A stale row wrote to a different account.");
        account.XUIDOnly = "123";

        wire.RevisionStatus = 401; before = wire.Writes;
        var api = new XboxApiClient(account.XAUTH, wire);
        var failed = await api.WriteTitleStatWithDiagnosticsAsync("123", StatTransport.Scid, "Kills", new JValue(12));
        if (failed.StatusCode != 401 || wire.Writes != before) throw new Exception("Stat write proceeded after authentication read failure.");
        File.WriteAllText(Path.Combine(root, "stats-editor-checks.txt"),
            "PASS: every shipped title/event classification, supported/unsupported rows, strict value types, request body/revisions, verified versus delayed readback, title/account changes, and no write after authentication read failure. Synthetic transport only.");
        vm.ReleaseClient(); api.Dispose();
    }

    private sealed class StatTransport : HttpMessageHandler
    {
        public const string Scid = "11111111-1111-1111-1111-111111111111";
        public int Requests, Writes, WriteStatus = 204, RevisionStatus = 200;
        public long PreviousRevision = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 100;
        public bool Propagate = true;
        public string Current = "1";
        public string TitleId = "309062877";
        public JObject? LastWrite;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            var code = 200; string body;
            if (host == "statswrite.xboxlive.com")
            {
                if (request.Method.Method != "PATCH") throw new Exception("Unexpected stat method.");
                LastWrite = JObject.Parse(await request.Content!.ReadAsStringAsync(token)); Writes++;
                code = WriteStatus; body = "{}";
                if (code is >= 200 and < 300 && Propagate) Current = LastWrite["stats"]?["title"]?["Kills"]?["value"]?.ToString() ?? "";
            }
            else if (host == "userstats.xboxlive.com" && request.Method == HttpMethod.Get)
            {
                if (path.EndsWith("/Kills")) code = RevisionStatus;
                body = new JObject { ["stats"] = new JArray(new JObject { ["name"] = "Kills", ["type"] = "Integer", ["value"] = Current,
                    ["scid"] = Scid, ["revision"] = PreviousRevision }) }.ToString();
            }
            else if (host == "userstats.xboxlive.com") body = "{\"statListsCollection\":[]}";
            else if (host == "titlehub.xboxlive.com") body = "{\"titles\":[{\"titleId\":\"" + TitleId + "\",\"name\":\"Fixture\",\"serviceConfigId\":\"" + Scid + "\"}]}";
            else if (host == "achievements.xboxlive.com") body = "{\"achievements\":[]}";
            else throw new Exception("Unexpected host in offline stat check: " + host);
            return new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
