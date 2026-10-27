using System.Text;
using System.Text.Json;

namespace AchievementLabs.Core;

// Diagnostic text is deliberately limited to public envelope structure, never token payloads.
public static class PresenceAuthorizationMetadata
{
    private sealed record Envelope(string Format, string Algorithm, string Encryption, string? KeyType, string? KeyReference);
    private static readonly HashSet<string> Algorithms = ["RS256", "RS384", "RS512", "ES256", "ES384", "ES512", "HS256", "HS384", "HS512", "RSA1_5", "RSA-OAEP", "RSA-OAEP-256", "dir"];
    private static readonly HashSet<string> Encryptions = ["A128CBC-HS256", "A192CBC-HS384", "A256CBC-HS512", "A128GCM", "A192GCM", "A256GCM"];

    public static string Compare(string macAuthorization, string pcAuthorization)
    {
        var mac = Read(macAuthorization); var pc = Read(pcAuthorization);
        var reference = mac.KeyReference == null || pc.KeyReference == null || mac.KeyType != pc.KeyType
            ? "not comparable (a matching type of public key reference is unavailable)"
            : mac.KeyReference == pc.KeyReference ? "matches" : "differs";
        return $"Current Mac account token: {mac.Format}; algorithm {mac.Algorithm}; encryption {mac.Encryption}.\n" +
            $"PC-paired token: {pc.Format}; algorithm {pc.Algorithm}; encryption {pc.Encryption}.\n" +
            $"Public header key reference: {reference}.\n" +
            "This compares public envelope metadata only. Matching references do not mean matching permissions; different references alone do not prove different permissions. Encrypted account/title/device claims were not decoded. No network requests or credential values are included in this report.";
    }

    private static Envelope Read(string authorization)
    {
        var unknown = new Envelope("opaque or unrecognized", "unavailable", "unavailable", null, null);
        if (authorization.Length > 131072 || !authorization.StartsWith("XBL3.0 x=", StringComparison.Ordinal)) return unknown;
        var separator = authorization.IndexOf(';');
        if (separator < 0) return unknown;
        var parts = authorization[(separator + 1)..].Split('.');
        if (parts.Length is not (3 or 5) || parts[0].Length is 0 or > 4096) return unknown;
        try
        {
            var encoded = parts[0].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            // Read only the first (public protected-header) segment, never payload/key segments.
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unknown;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) if (!names.Add(property.Name)) return unknown;
            string? Text(string name) => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            var algorithm = Text("alg"); var encryption = Text("enc");
            if (algorithm == null || (parts.Length == 5 && encryption == null)) return unknown;
            var keyType = new[] { "x5t#S256", "x5t", "kid" }.FirstOrDefault(n => Text(n) is { Length: > 0 and <= 128 });
            return new(parts.Length == 5 ? "encrypted JWT (5 segments)" : "signed JWT-shaped token (3 segments)",
                Algorithms.Contains(algorithm) ? algorithm : "other", encryption == null ? "not indicated" : Encryptions.Contains(encryption) ? encryption : "other",
                keyType, keyType == null ? null : Text(keyType));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { return unknown; }
    }
}
