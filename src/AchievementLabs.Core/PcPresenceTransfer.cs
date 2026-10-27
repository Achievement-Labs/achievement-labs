using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.Core;

public sealed record PcPresenceAuthorization(string Authorization, string Xuid, DateTimeOffset? ExpiresAt, bool IssuerExpiryKnown);

// Reads the existing encrypted PC-sync file, without importing event or renewal credentials.
public static class PcPresenceTransfer
{
    public static PcPresenceAuthorization Read(byte[] bytes, string pairingKey, string expectedXuid, DateTimeOffset now)
    {
        if (bytes.Length is 0 or > 131072 || pairingKey.Length != 43 ||
            pairingKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new InvalidDataException();
        byte[]? key = null, plain = null;
        try
        {
            key = Convert.FromBase64String(pairingKey.Replace('-', '+').Replace('_', '/') + "=");
            using var envelope = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            Unique(envelope.RootElement);
            var root = envelope.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException();
            var nonce = Convert.FromBase64String(root.GetProperty("nonce").GetString()!);
            var tag = Convert.FromBase64String(root.GetProperty("tag").GetString()!);
            var cipher = Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!);
            if (nonce.Length != 12 || tag.Length != 16 || cipher.Length is 0 or > 65536) throw new InvalidDataException();
            plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes("AchievementLabs-PC-Transfer-v1"));
            using var payload = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 8 });
            Unique(payload.RootElement);
            root = payload.RootElement;
            var expires = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("expiresAt").GetInt64());
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("kind").GetString() is not ("account" or "signIn") ||
                expires <= now || expires > now.AddMinutes(11)) throw new InvalidDataException();
            var xuid = root.GetProperty("xuid").GetString()!;
            if (!ulong.TryParse(xuid, out _) || xuid != expectedXuid) throw new InvalidDataException();
            var auth = root.GetProperty("authorization").GetString()!;
            if (auth.Length is < 12 or > 65536 || !auth.StartsWith("XBL3.0 x=", StringComparison.Ordinal) ||
                auth.Any(c => char.IsControl(c)) || auth.IndexOf(';') < 10) throw new InvalidDataException();
            // Native Xbox tokens may be opaque: do not invent an issuer expiry.
            // Transfer invitations expire separately. Opaque Xbox authorization
            // has no inferable expiry; do not substitute a local test deadline.
            DateTimeOffset? deadline = null;
            var value = root.GetProperty("authorizationExpiresAt");
            var known = value.ValueKind != JsonValueKind.Null;
            if (known)
            {
                if (!DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var authExpiry) || authExpiry <= now.AddMinutes(1))
                    throw new InvalidDataException();
                deadline = authExpiry;
            }
            return new(auth, xuid, deadline, known);
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static void Unique(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException();
    }
}
