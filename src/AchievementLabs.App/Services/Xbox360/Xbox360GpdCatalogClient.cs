using System.Security.Cryptography;
using System.Text.Json;

namespace AchievementLabs.Services.Xbox360;

/// <summary>Download-only client for the public GPD catalog published as assets of the gpd-catalog release (a prerelease, so updaters ignore it).</summary>
public sealed class Xbox360GpdCatalogClient(HttpClient http, string gpdDirectory)
{
    public sealed record Title(string TitleId, string Name, int Achievements, int Gamerscore, int Bytes, string Sha256, string Release);
    public static readonly Uri BaseUri = new("https://github.com/Achievement-Labs/achievement-labs/releases/download/");
    public static readonly string IssueUri = "https://github.com/Achievement-Labs/achievement-labs/issues/new?template=gpd-contribution.yml&title=";
    private const int IndexLimit = 4 * 1024 * 1024;
    private Dictionary<string, Title> titles = new(StringComparer.Ordinal);

    public string CachePath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gpdDirectory))!, "gpd-catalog-index.json");

    public static bool IsTitleId(string? id) =>
        id is { Length: 8 } && id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') && id != "FFFE07D1";

    /// <summary>GPDs are spread over gpd-catalog-N releases (1000 assets per release); index.json lives in gpd-catalog.</summary>
    public static bool IsRelease(string id) =>
        id.StartsWith("gpd-catalog-", StringComparison.Ordinal) && id.Length is > 12 and < 17 && id[12] != '0' && id[12..].All(char.IsAsciiDigit);

    public static Dictionary<string, Title> Parse(byte[] json)
    {
        if (json.Length > IndexLimit) throw new InvalidDataException("The GPD catalog index is too large.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1 ||
            !root.TryGetProperty("titles", out var list) || list.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unsupported GPD catalog.");
        var result = new Dictionary<string, Title>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateObject())
        {
            var t = item.Value;
            var size = t.GetProperty("bytes").GetInt32(); var sha = t.GetProperty("sha256").GetString() ?? "";
            var release = t.TryGetProperty("release", out var r) ? r.GetString() ?? "" : "";
            if (!IsTitleId(item.Name) || size <= 0 || size > Xbox360GpdTemplate.MaxBytes || sha.Length != 64 ||
                !sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || !IsRelease(release))
                throw new InvalidDataException($"Invalid GPD catalog entry {item.Name}.");
            result[item.Name] = new(item.Name, t.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                t.TryGetProperty("achievements", out var a) ? a.GetInt32() : 0,
                t.TryGetProperty("gamerscore", out var g) ? g.GetInt32() : 0, size, sha, release);
        }
        return result;
    }

    /// <summary>Loads the published index, falling back to the last verified copy when offline.</summary>
    public async Task<IReadOnlyCollection<Title>> LoadAsync(CancellationToken token)
    {
        try
        {
            var bytes = await GetAsync(new Uri(BaseUri, "gpd-catalog/index.json"), IndexLimit, token);
            titles = Parse(bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var staged = CachePath + ".partial";
            await File.WriteAllBytesAsync(staged, bytes, token);
            File.Move(staged, CachePath, true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (!File.Exists(CachePath)) throw new IOException("The GPD catalog is unavailable: " + error.Message, error);
            titles = Parse(await File.ReadAllBytesAsync(CachePath, token));
        }
        return titles.Values;
    }

    /// <summary>Downloads one title's GPD into the local GPD folder after checking its size and SHA-256.</summary>
    public async Task<string> DownloadAsync(string titleId, CancellationToken token)
    {
        if (!titles.TryGetValue(titleId, out var title)) throw new InvalidOperationException("That title is not in the GPD catalog.");
        var data = await GetAsync(new Uri(BaseUri, $"{title.Release}/{titleId}.gpd"), Xbox360GpdTemplate.MaxBytes, token);
#if NET9_0_OR_GREATER
        if (data.Length != title.Bytes || Convert.ToHexStringLower(SHA256.HashData(data)) != title.Sha256)
#else
        if (data.Length != title.Bytes || Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != title.Sha256)
#endif
            throw new InvalidDataException("The downloaded GPD did not match the catalog checksum.");
        Directory.CreateDirectory(gpdDirectory);
        var path = Path.Combine(gpdDirectory, titleId + ".gpd"); var staged = path + ".partial";
        await File.WriteAllBytesAsync(staged, data, token);
        File.Move(staged, path, true);
        return path;
    }

    private async Task<byte[]> GetAsync(Uri uri, int limit, CancellationToken token)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response exceeds size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("Response exceeds size limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
