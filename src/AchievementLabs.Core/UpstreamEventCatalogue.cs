using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>Additive public data updates. Author-maintained mappings always win.</summary>
public sealed class UpstreamEventCatalogue
{
    public const string BundledSourceCommit = "938a9ba0f746fcb20cee3588f992356df92bf38b";
    private const string SourceRoot = "https://raw.githubusercontent.com/ethanwp28/achievement-labs/";
    private readonly string cachePath;
    private readonly HashSet<string> reserved;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private sealed record Snapshot(string Commit, DateTime CheckedUtc, JObject Data, Dictionary<string, string> Templates);
    private Snapshot current = new(BundledSourceCommit, DateTime.MinValue, new JObject { ["SupportedTitleIDs"] = new JArray() }, new());
    public string Status { get; private set; } = "Bundled event data loaded; upstream checks add new titles only.";
    public string[] TitleIds => Volatile.Read(ref current).Templates.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    public UpstreamEventCatalogue(string path, IEnumerable<string> reservedTitleIds)
    {
        cachePath = path;
        reserved = reservedTitleIds.ToHashSet(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024 * 1024) throw new InvalidDataException();
            var saved = Parse(File.ReadAllText(path));
            var commit = (string?)saved["commit"] ?? "";
            if (!Regex.IsMatch(commit, "^[a-f0-9]{40}$")) throw new InvalidDataException();
            var data = saved["data"] as JObject ?? throw new InvalidDataException();
            var rawTemplates = saved["templates"] as JObject ?? throw new InvalidDataException();
            var templates = new Dictionary<string, string>();
            var safe = new JObject();
            foreach (var entry in rawTemplates.Properties())
            {
                if (reserved.Contains(entry.Name)) continue;
                var block = data[entry.Name] as JObject ?? throw new InvalidDataException();
                var template = entry.Value.Type == JTokenType.String ? entry.Value.ToString() : throw new InvalidDataException();
                ValidateAsync(entry.Name, block, template, CancellationToken.None).GetAwaiter().GetResult();
                safe[entry.Name] = block.DeepClone(); templates.Add(entry.Name, template);
            }
            safe["SupportedTitleIDs"] = new JArray(templates.Keys);
            current = new(commit, (DateTime?)saved["checkedUtc"] ?? DateTime.MinValue, safe, templates);
            Status = $"Loaded {templates.Count:N0} cached upstream titles (Testing).";
        }
        catch { Status = "Upstream event cache could not be validated; bundled mappings retained."; }
    }

    public bool Contains(string titleId) => Volatile.Read(ref current).Templates.ContainsKey(titleId);
    public EventTitleMapping Title(string titleId)
    {
        var state = Volatile.Read(ref current);
        return new(((JObject)state.Data[titleId]!["Achievements"]!).Properties().Select(p => p.Name).ToArray());
    }
    public async Task<EventPayloads> PayloadsAsync(string titleId, string achievementId, string xuid, CancellationToken ct)
    {
        var state = Volatile.Read(ref current);
        var catalogue = new EventCatalog(state.Data, (id, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(state.Templates[id]); });
        return new((await catalogue.BuildPayloadsAsync(titleId, achievementId, xuid, DateTime.UtcNow, ct).ConfigureAwait(false)).ToArray());
    }

    public async Task RefreshAsync(HttpClient http, bool force = false, CancellationToken ct = default)
    {
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = Volatile.Read(ref current);
            if (!force && DateTime.UtcNow - previous.CheckedUtc < TimeSpan.FromHours(1)) return;
            var head = Parse(await ReadAsync(http, "https://api.github.com/repos/ethanwp28/achievement-labs/commits/main", 1024 * 1024, ct).ConfigureAwait(false));
            var commit = (string?)head["sha"] ?? "";
            if (!Regex.IsMatch(commit, "^[a-f0-9]{40}$")) throw new InvalidDataException("Invalid upstream commit.");
            var data = (JObject)previous.Data.DeepClone();
            var templates = new Dictionary<string, string>(previous.Templates, StringComparer.Ordinal);
            int added = 0, skipped = 0;
            if (commit != previous.Commit || force)
            {
                var upstream = Parse(await ReadAsync(http, SourceRoot + commit + "/src/AchievementLabs.App/Events/Data.json", 64 * 1024 * 1024, ct).ConfigureAwait(false));
                var ids = upstream["SupportedTitleIDs"] as JArray ?? throw new InvalidDataException("Upstream supported-title list is missing.");
                if (ids.Count > 5000) throw new InvalidDataException("Upstream title limit exceeded.");
                foreach (var id in ids.Select(i => i.ToString()).Distinct(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (reserved.Contains(id) || templates.ContainsKey(id)) continue;
                    if (!Regex.IsMatch(id, "^[0-9]{1,10}$") || upstream[id] is not JObject block || (bool?)block["Enabled"] == false) { skipped++; continue; }
                    string template;
                    try { template = await ReadAsync(http, SourceRoot + commit + "/src/AchievementLabs.App/Events/" + id + ".json", 1024 * 1024, ct).ConfigureAwait(false); }
                    catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { skipped++; continue; }
                    try { await ValidateAsync(id, block, template, ct).ConfigureAwait(false); }
                    catch (Exception e) when (e is InvalidDataException or JsonException or System.Text.Json.JsonException or ArgumentException or InvalidCastException or FormatException or KeyNotFoundException or InvalidOperationException) { skipped++; continue; }
                    data.Remove("SupportedTitleIDs");
                    data[id] = block.DeepClone(); templates.Add(id, template); added++;
                }
            }
            data.Remove("SupportedTitleIDs"); data["SupportedTitleIDs"] = new JArray(templates.Keys);
            var next = new Snapshot(commit, DateTime.UtcNow, data, templates);
            var saved = new JObject { ["commit"] = commit, ["checkedUtc"] = next.CheckedUtc, ["data"] = data, ["templates"] = JObject.FromObject(templates) };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, saved.ToString(Formatting.None), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); File.Move(temporary, cachePath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Volatile.Write(ref current, next);
            Status = $"Checked upstream {commit[..7]} · {added:N0} new titles added · {templates.Count:N0} cached titles (Testing) · {skipped:N0} entries skipped · {next.CheckedUtc.ToLocalTime():g}";
        }
        catch (OperationCanceledException) { throw; }
        catch { Status = "Could not update upstream event data. Existing mappings and cached titles retained; retry with Check now."; }
        finally { refreshGate.Release(); }
    }

    private static JObject Parse(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
    }
    private static async Task<string> ReadAsync(HttpClient http, string uri, int limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("AchievementLabs-EventUpdater/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Event download exceeds limit.");
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[81920];
        while (true) { var count = await input.ReadAsync(bytes, ct).ConfigureAwait(false); if (count == 0) break; if (buffer.Length + count > limit) throw new InvalidDataException("Event download exceeds limit."); buffer.Write(bytes, 0, count); }
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }
    private static async Task ValidateAsync(string id, JObject block, string template, CancellationToken ct)
    {
        if (!Regex.IsMatch(id, "^[0-9]{1,10}$") || template.Length > 1024 * 1024 || block["Achievements"] is not JObject achievements || achievements.Count == 0 || achievements.Count > 2000) throw new InvalidDataException();
        var catalog = new EventCatalog(new JObject { [id] = block.DeepClone(), ["SupportedTitleIDs"] = new JArray(id) }, (_, _) => Task.FromResult(template));
        foreach (var achievement in achievements.Properties())
        {
            // Auto-import only ordinary deterministic replacements; unsupported schemas need review.
            var ops = (achievement.Value as JObject ?? throw new InvalidDataException()).DescendantsAndSelf().OfType<JObject>().Where(o => o["ReplacementType"] != null).ToArray();
            if (ops.Length == 0 || ops.Length > 100 || ops.Any(o => (string?)o["ReplacementType"] != "Replace" || string.IsNullOrEmpty((string?)o["Target"]) || o["Replacement"]?.Type != JTokenType.String)) throw new InvalidDataException();
            var mapping = (JObject)achievement.Value;
            var first = mapping.Properties().First().Value as JObject ?? throw new InvalidDataException();
            var requests = first["ReplacementType"] != null ? new[]{mapping} : mapping.Properties().Select(p => (JObject)p.Value).ToArray();
            foreach (var request in requests) {
                var draft = template;
                foreach (var operation in request.Properties()) {
                    var op = (JObject)operation.Value;
                    var target = (string)op["Target"]!;
                    if (!draft.Contains(target, StringComparison.Ordinal)) throw new InvalidDataException("Replacement target is absent.");
                    draft = draft.Replace(target, (string)op["Replacement"]!, StringComparison.Ordinal);
                }
            }
            var bodies = await catalog.BuildPayloadsAsync(id, achievement.Name, "0", DateTime.UnixEpoch, ct).ConfigureAwait(false);
            if (bodies.Count == 0 || bodies.Count > 20) throw new InvalidDataException();
            foreach (var text in bodies)
            {
                using var strict = System.Text.Json.JsonDocument.Parse(text);
                var body = JObject.Parse(text)["data"]?["baseData"];
                if (body?["titleId"]?.ToString() != id || !Guid.TryParse((string?)body["serviceConfigId"], out _) || string.IsNullOrEmpty((string?)body["name"])) throw new InvalidDataException();
                if (JObject.Parse(text)["name"] is not JValue name || name.Type != JTokenType.String || !name.ToString().StartsWith("Microsoft.XboxLive.T" + id + ".", StringComparison.Ordinal)) throw new InvalidDataException();
            }
        }
    }
}
