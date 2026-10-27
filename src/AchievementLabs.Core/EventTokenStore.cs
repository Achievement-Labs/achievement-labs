using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AchievementLabs.Core;

public static class EventTokenStore
{
    public static string DefaultPath => AchievementLabsPaths.LocalFile("event-token.bin");

    public static async Task SaveAsync(string token, CancellationToken cancellationToken = default)
    {
        var plain = Encoding.UTF8.GetBytes(token);
        byte[] protectedBytes;
        try { protectedBytes = SecureUserData.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
        await File.WriteAllBytesAsync(DefaultPath, protectedBytes, cancellationToken);
    }

    public static async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DefaultPath)) return "";
        var protectedBytes = await File.ReadAllBytesAsync(DefaultPath, cancellationToken);
        var plain = SecureUserData.Unprotect(protectedBytes);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static void Delete()
    {
        if (File.Exists(DefaultPath)) File.Delete(DefaultPath);
    }
}

public static class EventTokenValidator
{
    public static string? GetUserHash(string value)
    {
        var match = Regex.Match(value?.Trim().Trim('"') ?? "", @"^(?:x:)?XBL3\.0\s+x=(?<hash>[^;\s]+);", RegexOptions.IgnoreCase);
        return match.Success && match.Groups["hash"].Value != "-" ? match.Groups["hash"].Value : null;
    }

    // Only a definite mismatch counts: an unreadable or redacted hash on either side
    // is not proof of a different account, and Xbox rejects a wrong token anyway.
    public static bool IsDifferentUser(string token, string authorization)
        => GetUserHash(token) is { } tokenHash && GetUserHash(authorization) is { } sessionHash
            && !string.Equals(tokenHash, sessionHash, StringComparison.Ordinal);

    public static bool TryNormalize(string? token, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(token)) return false;
        var value = token.Trim();

        // Accept the value copied directly from the `tickets` request header.
        var ticket = Regex.Match(value, "(?:tickets\\s*:\\s*)?\\\"1\\\"\\s*=\\s*\\\"(?<token>[^\\\"]+)\\\"", RegexOptions.IgnoreCase);
        if (ticket.Success) value = ticket.Groups["token"].Value;
        value = value.Trim().Trim('"');

        var start = value.IndexOf("x:XBL3.0 x=", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = value.IndexOf("XBL3.0 x=", StringComparison.OrdinalIgnoreCase);
        if (start > 0) value = value[start..];
        if (value.StartsWith("XBL3.0 x=", StringComparison.OrdinalIgnoreCase)) value = "x:" + value;

        var separator = value.IndexOf(';');
        if (!value.StartsWith("x:XBL3.0 x=", StringComparison.OrdinalIgnoreCase) || separator < 14 || separator == value.Length - 1)
            return false;
        normalized = value;
        return true;
    }

    public static bool TryValidate(string? token, out DateTimeOffset? expiresAt, out string message)
    {
        expiresAt = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            message = "No event token is saved.";
            return false;
        }

        if (!TryNormalize(token, out var value))
        {
            message = "The event token format could not be recognized. Copy the complete x:XBL3.0 value.";
            return false;
        }
        var jwt = value[(value.LastIndexOf(';') + 1)..];
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            message = "Event token ready · expiration will be verified by Xbox when it is used.";
            return true;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!document.RootElement.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds))
            {
                message = "Event token ready · expiration will be verified by Xbox when it is used.";
                return true;
            }
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                message = "The saved event token is expired.";
                return false;
            }
            message = $"Event token ready · expires {expiresAt.Value.LocalDateTime:g}";
            return true;
        }
        catch
        {
            message = "Event token ready · expiration will be verified by Xbox when it is used.";
            return true;
        }
    }
}
