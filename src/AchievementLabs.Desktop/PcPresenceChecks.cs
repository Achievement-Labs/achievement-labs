using AchievementLabs.Core;
using System.Text;

namespace AchievementLabs.Desktop;

internal static class PcPresenceChecks
{
    public static void Run()
    {
        string Token(string header) => "XBL3.0 x=synthetic;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(header)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".private-key.private-iv.private-claims.private-tag";
        var left = Token("{\"alg\":\"RSA-OAEP\",\"enc\":\"A256GCM\",\"kid\":\"private-reference-A\"}");
        var right = Token("{\"alg\":\"RSA-OAEP\",\"enc\":\"A256GCM\",\"kid\":\"private-reference-B\"}");
        var report = PresenceAuthorizationMetadata.Compare(left, right);
        if (!report.Contains("reference: differs") || !report.Contains("encrypted JWT (5 segments)") || report.Contains("private-") || report.Contains("XBL3.0")) throw new Exception("Metadata report leaked values or miscompared public headers.");
        if (!PresenceAuthorizationMetadata.Compare(left, left).Contains("reference: matches")) throw new Exception("Identical public references differed.");
        if (!PresenceAuthorizationMetadata.Compare("XBL3.0 x=synthetic;opaque-private-token", left).Contains("not comparable")) throw new Exception("Opaque token was guessed.");
        var duplicate = Token("{\"alg\":\"RSA-OAEP\",\"alg\":\"ES256\",\"enc\":\"A256GCM\"}");
        if (!PresenceAuthorizationMetadata.Compare(duplicate, left).Contains("opaque or unrecognized")) throw new Exception("Ambiguous metadata accepted.");
        var unsafeHeader = Token("{\"alg\":\"private-secret\",\"enc\":\"private-secret\",\"kid\":\"private-key-reference\"}");
        if (PresenceAuthorizationMetadata.Compare(unsafeHeader, left).Contains("private-")) throw new Exception("Non-allowlisted header text leaked.");
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddHours(1);
        var transfer = MobileTransfer.Create("XBL3.0 x=hash;synthetic-account", "123", "synthetic-event", expiry);
        var imported = PcPresenceTransfer.Read(transfer.Bytes, transfer.PairingKey, "123", now);
        if (imported.Authorization != "XBL3.0 x=hash;synthetic-account" || imported.Xuid != "123" || imported.ExpiresAt != expiry || !imported.IssuerExpiryKnown)
            throw new Exception("PC pairing round trip failed.");
        void Reject(byte[] bytes, string key, string xuid, DateTimeOffset time)
        {
            try { PcPresenceTransfer.Read(bytes, key, xuid, time); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidDataException or FormatException or System.Text.Json.JsonException) { return; }
            throw new Exception("Invalid PC pairing accepted.");
        }
        Reject(transfer.Bytes, transfer.PairingKey, "456", now);
        Reject(transfer.Bytes, transfer.PairingKey, "123", now.AddMinutes(11));
        Reject(transfer.Bytes, transfer.PairingKey, "123", now.AddMinutes(-12));
        Reject(transfer.Bytes, new string('A', 43), "123", now);
        Reject(new byte[131073], transfer.PairingKey, "123", now);
        var json = Encoding.UTF8.GetString(transfer.Bytes);
        Reject(Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")), transfer.PairingKey, "123", now);
        var broken = transfer.Bytes.ToArray();
        // Change authenticated ciphertext while keeping a syntactically valid envelope.
        var start = json.IndexOf("\"ciphertext\":\"", StringComparison.Ordinal) + "\"ciphertext\":\"".Length;
        broken[start] = broken[start] == (byte)'A' ? (byte)'B' : (byte)'A';
        Reject(broken, transfer.PairingKey, "123", now);
        foreach (var invalid in new[] {
            MobileTransfer.Create("XBL3.0 x=hash;synthetic", "123", "event", expiry, eventOnly:true),
            MobileTransfer.Create("XBL3.0 x=hash;synthetic", "123", "", now, signInOnly:true) })
            Reject(invalid.Bytes, invalid.PairingKey, "123", now);
        var signIn = MobileTransfer.Create("XBL3.0 x=hash;synthetic", "123", "", expiry, signInOnly:true);
        PcPresenceTransfer.Read(signIn.Bytes, signIn.PairingKey, "123", now);
        var opaque = MobileTransfer.Create("XBL3.0 x=hash;synthetic", "123", "", null, signInOnly:true);
        var bounded = PcPresenceTransfer.Read(opaque.Bytes, opaque.PairingKey, "123", now);
        if (bounded.IssuerExpiryKnown || bounded.ExpiresAt != null) throw new Exception("Opaque issuer expiry was invented.");
        var shortLived = MobileTransfer.Create("XBL3.0 x=hash;synthetic", "123", "", now.AddMinutes(5), signInOnly:true);
        if (PcPresenceTransfer.Read(shortLived.Bytes, shortLived.PairingKey, "123", now).ExpiresAt != now.AddMinutes(5)) throw new Exception("Known expiry ignored.");
    }
}
