using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AchievementLabs.Services.Xbox360;

public sealed record Exophase360Game(long Id, string Name)
{
    public string Label => $"{Name} · {Id}";
}

/// <summary>Public Exophase lookup only. Never sends Xbox tokens, profile files or signing data.</summary>
public sealed class Exophase360Client : IDisposable
{
    private readonly HttpClient http;
    public Exophase360Client(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementLabs/1.0 (Xbox360-reference-lookup)");
    }
    public async Task<IReadOnlyList<Exophase360Game>> FindGamesAsync(string name, bool xboxGamertag, CancellationToken cancellation = default)
    {
        name = name.Trim().Normalize(NormalizationForm.FormKC);
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsLetterOrDigit(c) && c is not (' ' or '_' or '-' or '.')))
            throw new InvalidDataException("Enter an Exophase profile name or Xbox gamertag, without a URL.");
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellation); total.CancelAfter(TimeSpan.FromSeconds(90));
        var html = await GetAsync(new Uri($"https://www.exophase.com/{(xboxGamertag ? "xbox/" : "")}user/{Uri.EscapeDataString(name)}/"),total.Token);
        var match = Regex.Match(html,@"playerProfileId\s*[=:]\s*['""]?([A-Za-z0-9_]+)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(250));
        if (!match.Success || match.Groups[1].Length > 80) throw new InvalidDataException("Could not read this public Exophase profile. Check the name and profile visibility.");
        var player = match.Groups[1].Value;
        var result = new Dictionary<long,Exophase360Game>();
        var size = html.Length;
        for (var page = 1; page <= 100; page++)
        {
            var json = await GetAsync(new Uri($"https://api.exophase.com/public/player/{Uri.EscapeDataString(player)}/games?page={page}&environment=&sort=1&showHidden=0"),total.Token);
            size += json.Length;
            if (size > 12 * 1024 * 1024) throw new InvalidDataException("Exophase profile is too large for one lookup.");
            using var doc = JsonDocument.Parse(json,new JsonDocumentOptions { MaxDepth = 32 });
            if (!doc.RootElement.TryGetProperty("success",out var success) || success.ValueKind != JsonValueKind.True ||
                !doc.RootElement.TryGetProperty("games",out var games) || games.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Exophase returned an unsupported game-list format.");
            if (games.GetArrayLength() == 0) return result.Values.OrderBy(g => g.Name,StringComparer.OrdinalIgnoreCase).ToArray();
            if (games.GetArrayLength() > 1000) throw new InvalidDataException("Too many Exophase games in one response.");
            foreach (var game in games.EnumerateArray())
            {
                if (!game.TryGetProperty("master_id",out var id) || !id.TryGetInt64(out var numeric) || numeric <= 0 ||
                    !game.TryGetProperty("meta",out var meta) || !meta.TryGetProperty("title",out var title) || title.ValueKind != JsonValueKind.String ||
                    !meta.TryGetProperty("platforms",out var platforms) || platforms.ValueKind != JsonValueKind.Array) continue;
                var is360 = platforms.EnumerateArray().Any(p => Platform(p,"slug") == "xbox360" || Platform(p,"name") == "xbox360");
                var gameName = title.GetString()?.Trim();
                if (is360 && !string.IsNullOrEmpty(gameName) && gameName.Length <= 300) result[numeric] = new(numeric,gameName);
            }
        }
        throw new InvalidDataException("Too many Exophase pages to finish this lookup. Try a smaller public profile.");
    }
    public async Task<IReadOnlyList<Xbox360ReferenceUnlock>> LoadDatesAsync(string name, bool xboxGamertag, long gameId, CancellationToken cancellation = default)
    {
        // Resolve the current public profile again; never reuse another user's player ID.
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsLetterOrDigit(c) && c is not (' ' or '_' or '-' or '.')) || gameId <= 0)
            throw new InvalidDataException("Invalid Exophase profile or game.");
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellation); total.CancelAfter(TimeSpan.FromSeconds(45));
        var html = await GetAsync(new Uri($"https://www.exophase.com/{(xboxGamertag ? "xbox/" : "")}user/{Uri.EscapeDataString(name)}/"),total.Token);
        var match = Regex.Match(html,@"playerProfileId\s*[=:]\s*['""]?([A-Za-z0-9_]+)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(250));
        if (!match.Success || match.Groups[1].Length > 80) throw new InvalidDataException("Could not read this public Exophase profile.");
        var json = await GetAsync(new Uri($"https://api.exophase.com/public/player/{Uri.EscapeDataString(match.Groups[1].Value)}/game/{gameId}/earned"),total.Token);
        return Xbox360TimestampPreview.Parse(json);
    }
    private static string Platform(JsonElement p,string field) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(field,out var v) && v.ValueKind == JsonValueKind.String
        ? string.Concat(v.GetString()!.Where(char.IsLetterOrDigit)).ToLowerInvariant() : "";
    private async Task<string> GetAsync(Uri uri, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        for (var redirects = 0; redirects < 4; redirects++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not ("www.exophase.com" or "api.exophase.com"))
                throw new InvalidDataException("Unexpected Exophase response location.");
            using var response = await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { uri = location.IsAbsoluteUri ? location : new Uri(uri,location); continue; }
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("Exophase blocked the lookup. Try again later or use the file-import fallback.");
            if ((int)response.StatusCode == 429) throw new InvalidOperationException("Exophase rate limit reached. Wait before trying again.");
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Exophase profile or game was not found.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 2*1024*1024) throw new InvalidDataException("Exophase response exceeds 2 MB.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(); var chunk = new byte[16384]; int read;
            while ((read = await input.ReadAsync(chunk,timeout.Token)) > 0)
            { if (buffer.Length+read > 2*1024*1024) throw new InvalidDataException("Exophase response exceeds 2 MB."); await buffer.WriteAsync(chunk.AsMemory(0,read),timeout.Token); }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        throw new InvalidDataException("Too many Exophase redirects.");
    }
    public void Dispose() => http.Dispose();
}
