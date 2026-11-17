#if WINDOWS
using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using AchievementLabs.Core;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace AchievementLabs.Desktop;

public sealed record Windows8Token(string Token, string TitleId, string Xuid, DateTimeOffset ExpiresAt)
{
    public bool IsValid => DateTimeOffset.UtcNow < ExpiresAt;
}

public static class Windows8LicenseClient
{
    private static readonly SemaphoreSlim StoreLock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static string StoreFile => AchievementLabsPaths.LocalFile("win8-license-tokens.bin");
    public static string DataDirectory => AchievementLabsPaths.LocalFile("Windows8Licenses");
    public sealed record Acquisition(bool Success, string TitleId, string Message, string? ReceiptPath, string? TokenPath, DateTimeOffset? ExpiresUtc, bool DataPreservationAlert);

    public static async Task<Windows8Token> AcquireAsync(string titleId, string xuid, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!uint.TryParse(titleId, out _) || !ulong.TryParse(xuid, out _)) throw new InvalidDataException("A numeric title and connected Xbox account are required.");
        var requestId = Guid.NewGuid().ToString("N");
        var script = Path.Combine(AppContext.BaseDirectory, "tools", "Acquire-Windows8License.ps1");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-MTA", "-ExecutionPolicy", "Bypass", "-File", script, "-TitleId", titleId, "-DataDirectory", DataDirectory, "-RequestId", requestId }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The license helper could not start.");
        // Always drain and let the controller finish cleanup, even when the view/app is cancelled.
        var output = DrainAsync(process.StandardOutput); var errors = DrainAsync(process.StandardError);
        await process.WaitForExitAsync();
        await Task.WhenAll(output, errors);
        cancellation.ThrowIfCancellationRequested();
        var resultFile = Path.Combine(DataDirectory, requestId + ".json");
        if (!File.Exists(resultFile)) throw new InvalidOperationException("The license helper did not return a result. Check Windows PowerShell and the bundled runtime.");
        var result = JsonSerializer.Deserialize<Acquisition>(await File.ReadAllTextAsync(resultFile, cancellation), Json)
            ?? throw new InvalidDataException("The license helper returned an empty result.");
        if (!result.Success || process.ExitCode != 0) throw new InvalidOperationException(result.Message);
        if (result.DataPreservationAlert) throw new InvalidOperationException("Token issued, but the helper detected changed game data. Inspect the private before/after copies before retrying.");
        if (result.TitleId != titleId || result.ExpiresUtc is not { } expires || expires <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The helper returned an expired token or a different title.");
        var tokenPath = Path.GetFullPath(result.TokenPath ?? throw new InvalidDataException("The helper returned no token file."));
        if (!tokenPath.StartsWith(Path.GetFullPath(DataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Token file is outside the private license directory.");
        var auth = BuildAuthorization(await File.ReadAllTextAsync(tokenPath, cancellation));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        await VerifyAccountAsync(http, auth, xuid, cancellation);
        var held = new Windows8Token(auth, titleId, xuid, expires);
        await SaveAsync(held, cancellation);
        return held;
    }
    public static async Task<Windows8Token> ImportCapturedAsync(string titleId, string xuid, string authorization, CancellationToken cancellation)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        await VerifyAccountAsync(http, authorization, xuid, cancellation);
        // Captured encrypted assertions do not expose an authoritative expiry. Limit local reuse.
        var held = new Windows8Token(authorization, titleId, xuid, DateTimeOffset.UtcNow.AddMinutes(15));
        await SaveAsync(held, cancellation);
        return held;
    }
    private static async Task SaveAsync(Windows8Token held, CancellationToken cancellation)
    {
        await StoreLock.WaitAsync(cancellation);
        try { var all = await ReadStoreAsync(); all.RemoveAll(t => t.TitleId == held.TitleId && t.Xuid == held.Xuid); all.Add(held); await WriteStoreAsync(all); }
        finally { StoreLock.Release(); }
    }
    private static async Task DrainAsync(StreamReader reader) { var buffer = new char[4096]; while (await reader.ReadAsync(buffer) > 0) { } }
    public static string BuildAuthorization(string tokenXml)
    {
        if (tokenXml.Length > 65536) throw new InvalidDataException("Unexpected token size.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 };
        using var reader = XmlReader.Create(new StringReader(tokenXml), settings);
        var doc = new XmlDocument { XmlResolver = null }; doc.Load(reader);
        if (doc.DocumentElement is not { LocalName: "jwt" } element || string.IsNullOrWhiteSpace(element.GetAttribute("t"))) throw new InvalidDataException("Expected a service-issued legacy Xbox token.");
        return "XBL2.0 x=" + element.OuterXml;
    }
    public static async Task VerifyAccountAsync(HttpClient http, string authorization, string expectedXuid, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://profile.xboxlive.com/users/me/id");
        message.Headers.TryAddWithoutValidation("Authorization", authorization);
        message.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        using var response = await http.SendAsync(message, cancellation);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Token account verification returned HTTP {(int)response.StatusCode}. No achievement request was sent.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        if (!json.RootElement.TryGetProperty("xuid", out var id) || id.ToString() != expectedXuid) throw new InvalidOperationException("Windows signed in to a different Xbox account. Match Windows sign-in to the account connected in Achievement Labs.");
    }
    public static async Task<Windows8Token?> ValidAsync(string titleId, string xuid)
    {
        await StoreLock.WaitAsync();
        try { return (await ReadStoreAsync()).FirstOrDefault(t => t.TitleId == titleId && t.Xuid == xuid && t.IsValid); }
        finally { StoreLock.Release(); }
    }
    public static async Task ForgetAsync(string titleId, string xuid)
    {
        await StoreLock.WaitAsync();
        try { var all = await ReadStoreAsync(); all.RemoveAll(t => t.TitleId == titleId && t.Xuid == xuid); await WriteStoreAsync(all); }
        finally { StoreLock.Release(); }
    }
    private static async Task<List<Windows8Token>> ReadStoreAsync()
    {
        if (!File.Exists(StoreFile)) return [];
        try
        {
            var plain = await new DataProtectionProvider().UnprotectAsync(CryptographicBuffer.CreateFromByteArray(await File.ReadAllBytesAsync(StoreFile)));
            return (JsonSerializer.Deserialize<List<Windows8Token>>(CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, plain)) ?? []).Where(t => t.IsValid).ToList();
        }
        catch { return []; }
    }
    private static async Task WriteStoreAsync(List<Windows8Token> tokens)
    {
        var plain = CryptographicBuffer.ConvertStringToBinary(JsonSerializer.Serialize(tokens.Where(t => t.IsValid)), BinaryStringEncoding.Utf8);
        var protectedData = await new DataProtectionProvider("LOCAL=user").ProtectAsync(plain);
        CryptographicBuffer.CopyToByteArray(protectedData, out var bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!);
        var temporary = StoreFile + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes); File.Move(temporary, StoreFile, true);
    }
}
#endif
