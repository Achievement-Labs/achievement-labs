using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Net;

namespace AchievementLabs.Core;

// Device identity and account-scoped peer trust only. Never contains Xbox tokens.
public sealed class LanPairingStore : IDisposable
{
    readonly string path;
    readonly HashSet<string> peers;
    readonly Dictionary<string, string> addresses = new(StringComparer.Ordinal);
    public X509Certificate2 Identity { get; }
    sealed record Saved(string Certificate, string[] Peers, Dictionary<string, string>? Addresses = null);
    public LanPairingStore() : this(AchievementLabsPaths.LocalFile("lan-pairing.protected")) { }
    internal LanPairingStore(string storePath)
    {
        path = storePath;
        if (!File.Exists(path)) { Identity = LanPairing.NewIdentity(); peers = []; return; }
        if (new FileInfo(path).Length > 131072) throw new InvalidDataException();
        var plain = SecureUserData.Unprotect(File.ReadAllBytes(path));
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(plain) ?? throw new InvalidDataException();
            if (saved.Peers.Length > 64 || saved.Certificate.Length > 32768) throw new InvalidDataException();
            var pfx = Convert.FromBase64String(saved.Certificate);
            try { Identity = LanPairing.LoadIdentity(pfx); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            if (!Identity.HasPrivateKey || Identity.NotAfter.ToUniversalTime() <= DateTime.UtcNow) { Identity.Dispose(); Identity = LanPairing.NewIdentity(); peers = []; }
            else
            {
                peers = new(saved.Peers, StringComparer.Ordinal);
                if (saved.Addresses?.Count > 64) throw new InvalidDataException();
                foreach (var item in saved.Addresses ?? [])
                    if (peers.Contains(item.Key) && IPAddress.TryParse(item.Value, out var address) && LanPairing.IsLocalAddress(address)) addresses[item.Key] = address.ToString();
            }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public bool IsTrusted(string fingerprint, string xuid) => peers.Contains(xuid + ":" + fingerprint);
    public IEnumerable<IPAddress> KnownAddresses(string xuid) => addresses.Where(p => p.Key.StartsWith(xuid + ":", StringComparison.Ordinal) && peers.Contains(p.Key)).Select(p => IPAddress.Parse(p.Value)).ToArray();
    public void Remember(string fingerprint, string xuid, string? address = null)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit) || !ulong.TryParse(xuid, out var id) || id == 0) throw new InvalidDataException();
        var key = xuid + ":" + fingerprint;
        if (peers.Count >= 64 && !peers.Contains(key)) throw new InvalidOperationException("Forget paired devices before adding more.");
        peers.Add(key);
        if (address != null)
        {
            if (!IPAddress.TryParse(address, out var ip) || !LanPairing.IsLocalAddress(ip)) throw new InvalidDataException();
            addresses[key] = ip.ToString();
        }
        var pfx = Identity.Export(X509ContentType.Pkcs12);
        byte[]? plain = null;
        try
        {
            plain = JsonSerializer.SerializeToUtf8Bytes(new Saved(Convert.ToBase64String(pfx), peers.ToArray(), addresses));
            var encrypted = SecureUserData.Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, encrypted); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(pfx); if (plain != null) CryptographicOperations.ZeroMemory(plain); }
    }
    public static void ForgetAll()
    {
        var path = AchievementLabsPaths.LocalFile("lan-pairing.protected");
        if (File.Exists(path)) File.Delete(path);
    }
    public void Dispose() => Identity.Dispose();
}
