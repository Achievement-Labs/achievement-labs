using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>Reads metadata only. True means event-based; false means title-based; null means unconfirmed.</summary>
public static class LibrarySupportDiscovery
{
    public static bool? Classify(JObject response)
    {
        if (response["achievements"] is not JArray { Count: > 0 } rows) return null;
        bool complete = true;
        foreach (var row in rows)
        {
            if (row is not JObject item || !Guid.TryParse((string?)item["serviceConfigId"], out var scid) || scid == Guid.Empty
                || item["progression"] is not JObject progress || progress["requirements"] is not JArray { Count: > 0 } requirements)
            { complete = false; continue; }
            foreach (var requirement in requirements)
            {
                if (requirement is not JObject value || !Guid.TryParse((string?)value["id"], out var id)) complete = false;
                else if (id != Guid.Empty) return true;
            }
        }
        if (!string.IsNullOrEmpty((string?)response["pagingInfo"]?["continuationToken"])) return null;
        return complete ? false : null;
    }

    public static async Task<bool?> ReadAsync(HttpClient http, string authorization, string xuid, string title, CancellationToken token)
    {
        if (xuid.Length is < 1 or > 20 || !xuid.All(char.IsAsciiDigit) || !uint.TryParse(title, out var id) || id == 0 || title != id.ToString())
            throw new ArgumentException("Invalid title/account identity.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://achievements.xboxlive.com/users/xuid({xuid})/achievements?titleId={title}&maxItems=1000");
        request.Headers.Add("Authorization", authorization);
        request.Headers.Add("x-xbl-contract-version", "4");
        request.Headers.Add("Accept-Language", "en-US");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        const int limit = 8 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Support response exceeds limit.");
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var data = new MemoryStream();
        var bytes = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(bytes, token)) > 0)
        {
            if (data.Length + count > limit) throw new InvalidDataException("Support response exceeds limit.");
            data.Write(bytes, 0, count);
        }
        return Classify(EventCatalogStorage.Parse(new UTF8Encoding(false, true).GetString(data.ToArray())));
    }
}
