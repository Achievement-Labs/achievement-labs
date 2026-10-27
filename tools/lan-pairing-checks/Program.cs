using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AchievementLabs.Core;

static void Check(bool value, string failure) { if (!value) throw new Exception(failure); }
async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OperationCanceledException or IOException or System.Security.Authentication.AuthenticationException) { return; }
    throw new Exception("Invalid operation was accepted.");
}

foreach (var ip in new[] { "127.0.0.1", "10.1.2.3", "192.168.1.1", "172.16.1.2", "172.31.255.254", "169.254.1.1" }) Check(LanPairing.IsLocalAddress(IPAddress.Parse(ip)), "Local address rejected.");
foreach (var ip in new[] { "8.8.8.8", "0.0.0.0", "172.32.0.1", "224.0.0.1", "::ffff:8.8.8.8" }) Check(!LanPairing.IsLocalAddress(IPAddress.Parse(ip)), "Public or multicast address accepted.");
Console.WriteLine("PASS: local IPv4 destinations only.");
AchievementLabs.Desktop.PcPresenceChecks.Run();
Console.WriteLine("PASS: known Xbox expiry retained, unknown expiry stays unknown without a twenty-minute deadline; expired/replayed/wrong-account/tampered transfers rejected.");

async Task Exchange(bool acceptHost, bool acceptClient, bool wrongAccount = false, string session = "")
{
    using var host = new LanPairing.Host(loopbackOnly: true);
    using var serverIdentity = LanPairing.NewIdentity(); using var clientIdentity = LanPairing.NewIdentity();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    string? serverCode = null, clientCode = null; var created = 0; var imported = false; var remembered = false;
    var sending = host.SendAsync(serverIdentity, _ => false, review => { serverCode = review.Code; return Task.FromResult(new LanPairing.Approval(acceptHost, true)); },
        () => { created++; return Task.FromResult(MobileTransfer.Create("XBL3.0 x=hash;synthetic-only", "123", "", null, signInOnly: true)); },
        (fp, save) => { Check(fp == LanPairing.Fingerprint(clientIdentity), "Unexpected client identity."); remembered = save; }, timeout.Token);
    var receiving = LanPairing.ReceiveAsync(new("127.0.0.1", host.Port, session), clientIdentity, _ => false,
        review => { clientCode = review.Code; return Task.FromResult(new LanPairing.Approval(acceptClient, true)); },
        (transfer, token) => { token.ThrowIfCancellationRequested(); var result = PcPresenceTransfer.Read(transfer.Bytes, transfer.PairingKey, wrongAccount ? "456" : "123", DateTimeOffset.UtcNow); imported = result.Authorization == "XBL3.0 x=hash;synthetic-only"; return Task.CompletedTask; }, timeout.Token);
    if (acceptHost && acceptClient && !wrongAccount && session == "")
    {
        await Task.WhenAll(sending, receiving);
        Check(serverCode == clientCode && serverCode?.Length == 9, "Comparison codes differ.");
        Check(created == 1 && imported && remembered && receiving.Result.Remember, "Transfer/acknowledgement failed.");
        Check(receiving.Result.PeerFingerprint == LanPairing.Fingerprint(serverIdentity), "Unexpected server identity.");
    }
    else
    {
        try { await receiving; throw new Exception("Invalid import succeeded."); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException) { }
        timeout.Cancel(); await Reject(() => sending);
        Check(!remembered && !imported, "Rejected transfer was imported or remembered.");
        if (!acceptHost || !acceptClient || session.Length > 0) Check(created == 0, "Authorization created before both approvals.");
    }
}
await Exchange(true, true); Console.WriteLine("PASS: mutual TLS, matching comparison code, encrypted synthetic transfer, same-account import, receipt acknowledgement.");
await Exchange(false, true); await Exchange(true, false); Console.WriteLine("PASS: either device declining prevents authorization creation and trust.");
await Exchange(true, true, wrongAccount: true); Console.WriteLine("PASS: different account rejected; no successful receipt/trust.");
await Exchange(true, true, session: new string('0', 32)); Console.WriteLine("PASS: substituted discovery invitation rejected before authorization creation.");

using (var host = new LanPairing.Host(loopbackOnly: true))
using (var udp = new UdpClient())
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    await udp.SendAsync(Encoding.ASCII.GetBytes("AL-LAN-PAIR-1?"), new IPEndPoint(IPAddress.Loopback, LanPairing.DiscoveryPort), timeout.Token);
    var reply = Encoding.ASCII.GetString((await udp.ReceiveAsync(timeout.Token)).Buffer);
    Check(reply.StartsWith($"AL-LAN-PAIR-1|{host.Port}|") && reply.Split('|').Length == 3 && !reply.Contains("123" + "synthetic"), "Invalid discovery reply.");
}
Console.WriteLine("PASS: local discovery returns invitation and port without account authorization.");

// Discovery itself is exercised, with broadcasts disabled: an address retained
// after manual pairing must still find the PC's newly selected TCP port.
using (var host = new LanPairing.Host(loopbackOnly: true))
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
{
    string? report = null;
    using (var departed = new UdpClient())
        await departed.SendAsync(Encoding.ASCII.GetBytes("AL-LAN-PAIR-1?"), new IPEndPoint(IPAddress.Loopback, LanPairing.DiscoveryPort), timeout.Token);
    await Task.Delay(100, timeout.Token);
    var found = await LanPairing.DiscoverOnInterfacesAsync([
        new(IPAddress.Loopback, IPAddress.Parse("255.0.0.0")),
        // Two independent bound source sockets; avoid requiring a loopback alias
        // to be installed on macOS just for this synthetic test.
        new(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"))
    ], [IPAddress.Loopback, IPAddress.Parse("8.8.8.8")], text => report = text, timeout.Token, broadcast: false);
    Check(found.Count == 1 && found[0].Port == host.Port && found[0].Address == "127.0.0.1", "Remembered unicast discovery failed or duplicated interfaces.");
    Check(report?.Contains("2 interfaces, 6 probes sent,") == true, "Interface retries were not exercised.");
    Check(!report!.Contains("127.") && !report.Contains("synthetic"), "Discovery report contains endpoint/account data.");
}
Console.WriteLine("PASS: remembered-address discovery without broadcasts, three retries on each independent source socket, duplicate replies merged; safe counters exclude addresses and credentials.");

foreach (var size in new[] { -1, 0, 1, 196609, int.MaxValue })
{
    var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, size);
    await Reject(async () => await LanPairing.ReadAsync<object>(new MemoryStream(prefix), CancellationToken.None));
}
var duplicate = Encoding.UTF8.GetBytes("{\"Accept\":true,\"Accept\":false}");
using (var frame = new MemoryStream())
{
    var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, duplicate.Length); frame.Write(prefix); frame.Write(duplicate); frame.Position = 0;
    await Reject(async () => await LanPairing.ReadAsync<object>(frame, CancellationToken.None));
}
Console.WriteLine("PASS: invalid lengths and duplicate JSON fields rejected.");

if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
{
    var folder = Path.Combine(Path.GetTempPath(), "Labs-LAN-synthetic-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder); var path = Path.Combine(folder, "test.protected");
    try
    {
        string fingerprint;
        using var peer = LanPairing.NewIdentity(); var peerFingerprint = LanPairing.Fingerprint(peer);
        using (var store = new LanPairingStore(path)) { fingerprint = LanPairing.Fingerprint(store.Identity); store.Remember(peerFingerprint, "123", "192.168.1.42"); }
        using (var loaded = new LanPairingStore(path))
        {
            Check(fingerprint == LanPairing.Fingerprint(loaded.Identity) && loaded.IsTrusted(peerFingerprint, "123") && !loaded.IsTrusted(peerFingerprint, "456"), "Trust not stable or not account scoped.");
            Check(loaded.KnownAddresses("123").Single().ToString() == "192.168.1.42" && !loaded.KnownAddresses("456").Any(), "Saved endpoint leaked between accounts or did not persist.");
        }
        // Previous preview stores have no Addresses field; keep their keys/trust.
        var legacyPlain = SecureUserData.Unprotect(File.ReadAllBytes(path));
        try
        {
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(legacyPlain)!.AsObject(); legacy.Remove("Addresses");
            var bytes = Encoding.UTF8.GetBytes(legacy.ToJsonString());
            try { File.WriteAllBytes(path, SecureUserData.Protect(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CryptographicOperations.ZeroMemory(legacyPlain); }
        using (var legacyStore = new LanPairingStore(path))
            Check(legacyStore.IsTrusted(peerFingerprint, "123") && !legacyStore.KnownAddresses("123").Any(), "Previous-preview trust migration failed.");
        var cipher = File.ReadAllBytes(path); cipher[0] ^= 0x80; File.WriteAllBytes(path, cipher);
        try { using var invalid = new LanPairingStore(path); throw new Exception("Tampered storage accepted."); }
        catch (CryptographicException) { }
    }
    finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(folder); }
    Console.WriteLine("PASS: OS-protected identity persists, trust is account scoped, tampered store rejected; isolated test data removed.");
}
Console.WriteLine("No Xbox services contacted; no account credentials used. All network tests bound to loopback.");
