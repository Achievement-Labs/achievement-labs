using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>Additive public data updates. Author-maintained mappings always win.</summary>
public sealed class UpstreamEventCatalogue
{
    public const string BundledSourceCommit = "1a76574db3ca4d5ee02f162a63cf0ce3123b3db9";
    private const string BundledArchiveDigest = "f763e3048d7fda9d9e226dec0f40211937d0ffd454083aa29a7cd3f5af67b159";
    private readonly string cachePath;
    private readonly HashSet<string> reserved;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private sealed record Snapshot(string Commit, DateTime CheckedUtc, JObject Data, Dictionary<string, string> Templates);
    private Snapshot current = new(BundledArchiveDigest, DateTime.MinValue, new JObject { ["SupportedTitleIDs"] = new JArray() }, new());
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
            if (info.Length > 128 * 1024 * 1024) throw new InvalidDataException();
            var saved = Parse(File.ReadAllText(path));
            var commit = (string?)saved["commit"] ?? "";
            if (!Regex.IsMatch(commit, "^(?:[a-f0-9]{40}|[a-f0-9]{64})$")) throw new InvalidDataException();
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
    public async Task<int> ImportPackAsync(string text, CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(text) > 32 * 1024 * 1024) throw new InvalidDataException("Event pack exceeds 32 MB.");
        var pack = Parse(text);
        if ((int?)pack["schemaVersion"] != 1 || pack["titles"] is not JObject titles || titles.Count > 5000)
            throw new InvalidDataException("Choose an event pack containing templates and achievement mappings, not a single game template.");
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = Volatile.Read(ref current);
            var data = (JObject)previous.Data.DeepClone();
            var templates = new Dictionary<string, string>(previous.Templates, StringComparer.Ordinal);
            int added = 0;
            foreach (var title in titles.Properties())
            {
                ct.ThrowIfCancellationRequested();
                if (reserved.Contains(title.Name) || templates.ContainsKey(title.Name)) continue;
                var template = (string?)title.Value["template"] ?? throw new InvalidDataException("Missing event template.");
                var recipes = title.Value["recipes"]?["recipes"] as JObject ?? throw new InvalidDataException("Missing achievement mappings.");
                var achievements = new JObject();
                foreach (var recipe in recipes.Properties())
                {
                    if (!Regex.IsMatch(recipe.Name, "^[0-9]{1,10}$") || recipe.Value is not JArray requests || requests.Count is < 1 or > 20) throw new InvalidDataException("Invalid achievement mapping.");
                    var converted = new JObject(); int r = 0;
                    foreach (var request in requests)
                    {
                        if (request is not JArray operations || operations.Count is < 1 or > 100) throw new InvalidDataException("Invalid replacement list.");
                        var mapped = new JObject(); int n = 0;
                        foreach (var op in operations)
                        {
                            if (op["target"]?.Type != JTokenType.String || op["value"]?.Type != JTokenType.String) throw new InvalidDataException("Invalid replacement.");
                            mapped[(n++).ToString()] = new JObject { ["ReplacementType"] = "Replace", ["Target"] = op["target"]!.DeepClone(), ["Replacement"] = op["value"]!.DeepClone() };
                        }
                        converted[(r++).ToString()] = mapped;
                    }
                    achievements[recipe.Name] = converted;
                }
                var block = new JObject { ["Achievements"] = achievements };
                await ValidateAsync(title.Name, block, template, ct).ConfigureAwait(false);
                data.Remove("SupportedTitleIDs"); data[title.Name] = block; templates.Add(title.Name, template); added++;
            }
            data.Remove("SupportedTitleIDs"); data["SupportedTitleIDs"] = new JArray(templates.Keys);
            var next = new Snapshot(previous.Commit, previous.CheckedUtc, data, templates);
            var saved = new JObject { ["commit"] = next.Commit, ["checkedUtc"] = next.CheckedUtc, ["data"] = data, ["templates"] = JObject.FromObject(templates) };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, saved.ToString(Formatting.None), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); File.Move(temporary, cachePath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Volatile.Write(ref current, next);
            Status = $"Imported {added:N0} new titles from event pack (Testing). Existing titles retained.";
            return added;
        }
        finally { refreshGate.Release(); }
    }
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
        var stage = "reading release metadata";
        try
        {
            var previous = Volatile.Read(ref current);
            if (!force && DateTime.UtcNow - previous.CheckedUtc < TimeSpan.FromHours(1)) return;
            var releaseText = await ReadAsync(http, "https://api.github.com/repos/Achievement-Labs/achievement-labs/releases?per_page=100", 4 * 1024 * 1024, ct).ConfigureAwait(false);
            using var releaseReader = new JsonTextReader(new StringReader(releaseText)) { MaxDepth = 64 };
            var releases = JArray.Load(releaseReader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (releases.Count > 100) throw new InvalidDataException("Release list limit exceeded.");
            var release = releases.OfType<JObject>().FirstOrDefault(r => (bool?)r["draft"] == false && (bool?)r["prerelease"] == false
                && Regex.IsMatch((string?)r["tag_name"] ?? "", @"^v[0-9]+\.[0-9]+\.[0-9]+(?:[-.][A-Za-z0-9.-]+)?$")
                && r["assets"] is JArray list && list.OfType<JObject>().Any(a => (string?)a["name"] == "Events.zip"))
                ?? throw new InvalidDataException("No stable release containing Events.zip was found.");
            if ((bool?)release["draft"] != false || (bool?)release["prerelease"] != false) throw new InvalidDataException("No stable published release.");
            var tag = (string?)release["tag_name"] ?? "";
            if (!Regex.IsMatch(tag, "^v[0-9]+\\.[0-9]+\\.[0-9]+(?:[-.][A-Za-z0-9.-]+)?$")) throw new InvalidDataException("Unexpected release tag.");
            var assets = (release["assets"] as JArray ?? throw new InvalidDataException("Release assets are missing."))
                .OfType<JObject>().Where(a => (string?)a["name"] == "Events.zip").ToArray();
            if (assets.Length != 1) throw new InvalidDataException("The release does not contain one Events.zip.");
            var asset = assets[0]; var digest = (string?)asset["digest"] ?? "";
            if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("Events.zip SHA-256 digest is missing.");
            var revision = digest[7..].ToLowerInvariant();
            var url = "https://github.com/Achievement-Labs/achievement-labs/releases/download/" + tag + "/Events.zip";
            if ((string?)asset["browser_download_url"] != url) throw new InvalidDataException("Unexpected archive location.");
            var data = (JObject)previous.Data.DeepClone();
            var templates = new Dictionary<string, string>(previous.Templates, StringComparer.Ordinal);
            int added = 0, skipped = 0;
            if (revision != previous.Commit || force)
            {
                stage = "downloading Events.zip";
                var bytes = await ReadBytesAsync(http, url, 32 * 1024 * 1024, ct).ConfigureAwait(false);
                if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != revision) throw new InvalidDataException("Archive checksum mismatch.");
                stage = "reading Events.zip";
                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                if (archive.Entries.Count > 2500 || archive.Entries.Sum(e => e.Length) > 256L * 1024 * 1024) throw new InvalidDataException("Archive expansion limit exceeded.");
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries) {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.StartsWith('/') || name.Split('/').Any(part => part is ".." or ".") || !name.StartsWith("Events/", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected archive path.");
                    if (name.EndsWith('/')) continue;
                    if (name[7..].Contains('/') || !entries.TryAdd(name[7..], entry)) throw new InvalidDataException("Ambiguous archive entry.");
                }
                if (!entries.TryGetValue("Data.json", out var sourceData)) throw new InvalidDataException("Events/Data.json is missing.");
                var upstream = Parse(ReadZip(sourceData, 128 * 1024 * 1024));
                if (upstream.Properties().Last().Name != "SupportedTitleIDs") throw new InvalidDataException("SupportedTitleIDs must be last in Data.json.");
                var ids = upstream["SupportedTitleIDs"] as JArray ?? throw new InvalidDataException("Supported-title list is missing.");
                if (ids.Count > 5000) throw new InvalidDataException("Title limit exceeded.");
                stage = "validating new titles";
                foreach (var id in ids.Select(i => i.ToString()).Distinct(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (reserved.Contains(id) || templates.ContainsKey(id)) continue;
                    if (!Regex.IsMatch(id, "^[0-9]{1,10}$") || upstream[id] is not JObject block || (bool?)block["Enabled"] == false || !entries.TryGetValue(id + ".json", out var entry)) { skipped++; continue; }
                    var template = ReadZip(entry, 1024 * 1024);
                    try { await ValidateAsync(id, block, template, ct).ConfigureAwait(false); }
                    catch (Exception e) when (e is InvalidDataException or JsonException or System.Text.Json.JsonException or ArgumentException or InvalidCastException or FormatException or KeyNotFoundException or InvalidOperationException) { skipped++; continue; }
                    data.Remove("SupportedTitleIDs"); data[id] = block.DeepClone(); templates.Add(id, template); added++;
                }
            }
            data.Remove("SupportedTitleIDs"); data["SupportedTitleIDs"] = new JArray(templates.Keys);
            var next = new Snapshot(revision, DateTime.UtcNow, data, templates);
            var saved = new JObject { ["commit"] = revision, ["release"] = tag, ["checkedUtc"] = next.CheckedUtc, ["data"] = data, ["templates"] = JObject.FromObject(templates) };
            stage = "saving the validated cache";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, saved.ToString(Formatting.None), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); File.Move(temporary, cachePath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Volatile.Write(ref current, next);
            Status = $"Checked {tag} Events.zip · {added:N0} new titles added · {templates.Count:N0} cached titles (Testing) · {skipped:N0} entries skipped · {next.CheckedUtc.ToLocalTime():g}";
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException e) { Status = $"Could not update event data while {stage}: HTTP {(int?)e.StatusCode}. Existing mappings and cache retained; retry Check now."; }
        catch (InvalidDataException e) { Status = $"Could not update event data while {stage}: {e.Message} Existing mappings and cache retained."; }
        catch (JsonException) { Status = $"Could not update event data while {stage}: invalid catalogue JSON. Existing mappings and cache retained."; }
        catch { Status = $"Could not update event data while {stage}. Existing mappings and cache retained; retry Check now."; }
        finally { refreshGate.Release(); }
    }

    private static string ReadZip(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("Archive entry exceeds its size limit.");
        using var input = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[81920];
        while (true) { var read = input.Read(buffer); if (read == 0) break; if (output.Length + read > limit) throw new InvalidDataException("Archive entry exceeds its size limit."); output.Write(buffer,0,read); }
        return new UTF8Encoding(false,true).GetString(output.ToArray()).TrimStart('\uFEFF');
    }
    private static JObject Parse(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
    }
    private static async Task<string> ReadAsync(HttpClient http, string uri, int limit, CancellationToken ct) =>
        Encoding.UTF8.GetString(await ReadBytesAsync(http, uri, limit, ct).ConfigureAwait(false)).TrimStart('\uFEFF');
    private static async Task<byte[]> ReadBytesAsync(HttpClient http, string uri, int limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("AchievementLabs-Fork-EventUpdater/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Event download exceeds limit.");
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[81920];
        while (true) { var count = await input.ReadAsync(bytes, ct).ConfigureAwait(false); if (count == 0) break; if (buffer.Length + count > limit) throw new InvalidDataException("Event download exceeds limit."); buffer.Write(bytes, 0, count); }
        return buffer.ToArray();
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
                    var replacement = (string)op["Replacement"]!;
                    var occurrences = (draft.Length - draft.Replace(target, "", StringComparison.Ordinal).Length) / target.Length;
                    if (replacement.Length > 1024 * 1024 || (long)draft.Length + (long)occurrences * (replacement.Length - target.Length) > 2 * 1024 * 1024) throw new InvalidDataException("Event expansion limit exceeded.");
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
