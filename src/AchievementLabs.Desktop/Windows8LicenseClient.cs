#if WINDOWS
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using AchievementLabs.Core;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace AchievementLabs.Desktop;

public sealed record Windows8Token(string Token, string TitleId, string Xuid, DateTimeOffset ExpiresAt)
{
    public bool IsValid => DateTimeOffset.UtcNow < ExpiresAt;
    public bool SettingsMetadataChanged { get; init; }
}

public static class Windows8LicenseClient
{
    private static readonly SemaphoreSlim StoreLock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static string StoreFile => AchievementLabsPaths.LocalFile("win8-license-tokens.bin");
    public static string DataDirectory => AchievementLabsPaths.LocalFile("Windows8Licenses");
    public sealed record Acquisition(bool Success, string TitleId, string Message, string? ReceiptPath, string? TokenPath, DateTimeOffset? ExpiresUtc, bool DataPreservationAlert);

    public static async Task<Windows8Token> AcquireAsync(string titleId, string xuid, CancellationToken cancellation)
        => await AcquireCoreAsync(titleId, xuid, null, cancellation);

    public static async Task<Windows8Token> AcquireFromReceiptAsync(string titleId, string xuid, string receiptPath, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(receiptPath) || !File.Exists(receiptPath) ||
            !string.Equals(Path.GetFileName(receiptPath), "pop.bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select an existing pop.bin receipt.");
        return await AcquireCoreAsync(titleId, xuid, Path.GetFullPath(receiptPath), cancellation);
    }

    private static async Task<Windows8Token> AcquireCoreAsync(string titleId, string xuid, string? receiptPath, CancellationToken cancellation)
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
        if (receiptPath != null) { start.ArgumentList.Add("-ReceiptPath"); start.ArgumentList.Add(receiptPath); }
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
        if (result.TitleId != titleId || result.ExpiresUtc is not { } expires || expires <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The helper returned an expired token or a different title.");
        var tokenPath = Path.GetFullPath(result.TokenPath ?? throw new InvalidDataException("The helper returned no token file."));
        if (!tokenPath.StartsWith(Path.GetFullPath(DataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Token file is outside the private license directory.");
        if (result.DataPreservationAlert)
        {
            var runDirectory = Path.GetDirectoryName(Path.GetDirectoryName(tokenPath)!)
                ?? throw new InvalidDataException("The token run directory is missing.");
            var runsRoot = Path.GetFullPath(Path.Combine(DataDirectory, "runs")) + Path.DirectorySeparatorChar;
            if (!runDirectory.StartsWith(runsRoot, StringComparison.OrdinalIgnoreCase) ||
                !await IsSettingsMetadataOnlyAlertAsync(runDirectory, cancellation))
                throw new InvalidOperationException("Token issued, but game data changed beyond the verified Windows settings metadata pattern. Private before/after copies remain; no token was held.");
        }
        var auth = BuildAuthorization(await File.ReadAllTextAsync(tokenPath, cancellation));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        await VerifyAccountAsync(http, auth, xuid, cancellation);
        var held = new Windows8Token(auth, titleId, xuid, expires) { SettingsMetadataChanged = result.DataPreservationAlert };
        await SaveAsync(held, cancellation);
        return held;
    }

    internal static async Task<bool> IsSettingsMetadataOnlyAlertAsync(string runDirectory, CancellationToken cancellation)
    {
        var before = Path.Combine(runDirectory, "data-before-private");
        var after = Path.Combine(runDirectory, "data-after-private");
        if (!Directory.Exists(before) || !Directory.Exists(after)) return false;
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "token-result.json"), cancellation));
        using var lifecycle = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "temporary-helper.json"), cancellation));
        static bool Yes(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        if (!Yes(report.RootElement, "tokenIssued") || !Yes(report.RootElement, "nativeXboxJwt") ||
            !Yes(report.RootElement, "privateTokenExported") || !Yes(report.RootElement, "helperStopped") ||
            !Yes(report.RootElement, "privateSessionRemoved") || !Yes(report.RootElement, "helperRegistrationRemoved") ||
            !Yes(lifecycle.RootElement, "registrationRemoved") || !Yes(lifecycle.RootElement, "dataPreservationChecked") ||
            lifecycle.RootElement.TryGetProperty("cleanupFailureType", out _) ||
            lifecycle.RootElement.TryGetProperty("failureType", out _)) return false;
        static Dictionary<string, string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), path => path, StringComparer.OrdinalIgnoreCase);
        var oldFiles = Files(before);
        var newFiles = Files(after);
        if (oldFiles.Count == 0 || oldFiles.Keys.Except(newFiles.Keys, StringComparer.OrdinalIgnoreCase).Any()) return false;
        var changed = false;
        foreach (var (relative, oldPath) in oldFiles)
        {
            var newPath = newFiles[relative];
            if ((File.GetAttributes(oldPath) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(newPath) & FileAttributes.ReparsePoint) != 0) return false;
            if (await SameFileAsync(oldPath, newPath, cancellation)) continue;
            if (!relative.Equals(Path.Combine("Settings", "settings.dat"), StringComparison.OrdinalIgnoreCase) ||
                !await SameHiveExceptMetadataAsync(oldPath, newPath, cancellation)) return false;
            changed = true;
        }
        foreach (var relative in newFiles.Keys.Except(oldFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if ((!relative.Equals(Path.Combine("Settings", "settings.dat.LOG1"), StringComparison.OrdinalIgnoreCase) &&
                 !relative.Equals(Path.Combine("Settings", "settings.dat.LOG2"), StringComparison.OrdinalIgnoreCase)) ||
                new FileInfo(newFiles[relative]).Length > 1024 * 1024 ||
                (File.GetAttributes(newFiles[relative]) & FileAttributes.ReparsePoint) != 0) return false;
            changed = true;
        }
        return changed;
    }

    private static async Task<bool> SameFileAsync(string first, string second, CancellationToken cancellation)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        await using var a = File.OpenRead(first);
        await using var b = File.OpenRead(second);
        var firstHash = await SHA256.HashDataAsync(a, cancellation);
        var secondHash = await SHA256.HashDataAsync(b, cancellation);
        return firstHash.AsSpan().SequenceEqual(secondHash);
    }

    private static async Task<bool> SameHiveExceptMetadataAsync(string first, string second, CancellationToken cancellation)
    {
        var a = await File.ReadAllBytesAsync(first, cancellation);
        var b = await File.ReadAllBytesAsync(second, cancellation);
        if (a.Length != b.Length || a.Length < 8192 || a.Length > 1024 * 1024 || a.Length % 4096 != 0 ||
            System.Text.Encoding.ASCII.GetString(a, 0, 4) != "regf" ||
            System.Text.Encoding.ASCII.GetString(b, 0, 4) != "regf" ||
            System.Text.Encoding.ASCII.GetString(a, 4096, 4) != "hbin" ||
            System.Text.Encoding.ASCII.GetString(b, 4096, 4) != "hbin" ||
            BitConverter.ToInt32(a, 4) != BitConverter.ToInt32(a, 8) ||
            BitConverter.ToInt32(b, 4) != BitConverter.ToInt32(b, 8)) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (i is >= 4 and <= 19 or >= 168 and <= 175 or >= 508 and <= 511 or >= 4148 and <= 4151) continue;
            return false;
        }
        return true;
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
