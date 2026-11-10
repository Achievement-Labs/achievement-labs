using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AchievementLabs.Core;

/// <summary>Shared read cooldowns. Does not intercept or retry writes.</summary>
public sealed class XboxReadThrottle(HttpMessageHandler transport) : DelegatingHandler(transport)
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RetryAt = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object DiskGate = new();
    private static readonly Lazy<bool> Loaded = new(Load);
    private static bool Offline => AppContext.TryGetSwitch("AchievementLabs.OfflineChecks", out var value) && value;
    private static bool Xbox(Uri uri) => uri.Host.EndsWith(".xboxlive.com", StringComparison.OrdinalIgnoreCase);
    public static DateTimeOffset RetryAfter(string host)
    {
        _ = Loaded.Value;
        return RetryAt.GetValueOrDefault(host);
    }
    private static HttpRequestException Limited(DateTimeOffset until) => new(
        $"Xbox reads paused until {until.LocalDateTime:t}; cached data remains available.", null, HttpStatusCode.TooManyRequests);
    public static void Check(Uri uri)
    {
        if (Xbox(uri) && RetryAfter(uri.Host) is var until && until > DateTimeOffset.UtcNow) throw Limited(until);
    }
    public static void Observe(Uri uri, HttpResponseMessage response)
    {
        if (!Xbox(uri) || response.StatusCode != HttpStatusCode.TooManyRequests) return;
        var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(5);
        var until = DateTimeOffset.UtcNow + (delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1));
        RetryAt.AddOrUpdate(uri.Host, until, (_, previous) => previous > until ? previous : until);
        if (!Offline)
            lock (DiskGate)
                try
                {
                    var file = AchievementLabsPaths.LocalFile("xbox-read-cooldowns.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(RetryAt.Where(p => p.Value > DateTimeOffset.UtcNow).ToDictionary(p => p.Key,p => p.Value)));
                    File.Move(file + ".tmp", file, true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        throw Limited(RetryAt[uri.Host]);
    }
    private static bool Load()
    {
        if (Offline) return true;
        try
        {
            var path = AchievementLabsPaths.LocalFile("xbox-read-cooldowns.json");
            if (File.Exists(path) && new FileInfo(path).Length < 16384)
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) ?? new())
                    if (pair.Key.EndsWith(".xboxlive.com", StringComparison.OrdinalIgnoreCase) && pair.Value > DateTimeOffset.UtcNow)
                        RetryAt.TryAdd(pair.Key,pair.Value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return true;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is not { } uri || !Xbox(uri)) return await base.SendAsync(request,ct);
        Check(uri);
        var response = await base.SendAsync(request,ct);
        try { Observe(uri,response); return response; }
        catch { response.Dispose(); throw; }
    }
}
