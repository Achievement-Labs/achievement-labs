using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.Core;

// A separate, opt-in transport. No HTTP routes, account identifiers in discovery,
// automatic sign-in, certificate installation, or public Internet listener.
public static class LanPairing
{
    public const int DiscoveryPort = 40761;
    const int MaxFrame = 196608;
    const string Probe = "AL-LAN-PAIR-1?";
    public sealed record Candidate(string Address, int Port, string Session)
    {
        public override string ToString() => $"Windows app at {Address}:{Port}";
    }
    public sealed record Review(string Code, string PeerFingerprint, bool Remembered);
    public sealed record Approval(bool Accept, bool Remember);
    public sealed record Received(MobileTransferFile Transfer, string PeerFingerprint, bool Remember);

    public static bool IsLocalAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254;
    }

    public static IReadOnlyList<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address).Where(a => IsLocalAddress(a) && !IPAddress.IsLoopback(a)).Distinct().ToList();

    public static X509Certificate2 NewIdentity()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Achievement Labs local pairing", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var pfx = generated.Export(X509ContentType.Pkcs12);
        try { return LoadIdentity(pfx); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    internal static X509Certificate2 LoadIdentity(byte[] pfx) => X509CertificateLoader.LoadPkcs12(pfx, null,
        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public sealed class Host : IDisposable
    {
        readonly TcpListener listener;
        readonly UdpClient discovery;
        readonly CancellationTokenSource stop = new();
        readonly string session = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        int disposed;
        readonly int port;
        int discoveryQueries, discoveryReplies;
        string discoveryError = "none";
        public string DiscoveryStatus => $"Discovery requests received: {Volatile.Read(ref discoveryQueries)}; replies sent: {Volatile.Read(ref discoveryReplies)}; last socket error: {discoveryError}.";
        public int Port => port;
        public Host() : this(false) { }
        internal Host(bool loopbackOnly)
        {
            var bindAddress = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
            listener = new TcpListener(bindAddress, 0);
            discovery = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                discovery.Client.Bind(new IPEndPoint(bindAddress, DiscoveryPort));
                listener.Start(1);
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _ = AdvertiseAsync();
            }
            catch { listener.Stop(); discovery.Dispose(); stop.Dispose(); throw; }
        }
        async Task AdvertiseAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    UdpReceiveResult packet;
                    try { packet = await discovery.ReceiveAsync(stop.Token); }
                    catch (SocketException ex)
                    {
                        // Windows may report an ICMP reply from a scanner that
                        // has already closed its socket. Keep serving later probes.
                        discoveryError = ex.SocketErrorCode.ToString();
                        await Task.Delay(100, stop.Token);
                        continue;
                    }
                    if (!IsLocalAddress(packet.RemoteEndPoint.Address) || !packet.Buffer.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Probe))) continue;
                    Interlocked.Increment(ref discoveryQueries);
                    var reply = Encoding.ASCII.GetBytes($"AL-LAN-PAIR-1|{Port}|{session}");
                    try { await discovery.SendAsync(reply, packet.RemoteEndPoint, stop.Token); Interlocked.Increment(ref discoveryReplies); }
                    catch (SocketException ex) { discoveryError = ex.SocketErrorCode.ToString(); }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
        // One connection per invitation, including failed/rejected attempts. Restart is explicit.
        public async Task SendAsync(X509Certificate2 identity, Func<string, bool> trusted,
            Func<Review, Task<Approval>> confirm, Func<Task<MobileTransferFile>> create,
            Action<string, bool> completed, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
            listener.Stop(); discovery.Close();
            if (!IsLocalAddress(((IPEndPoint)tcp.Client.RemoteEndPoint!).Address)) throw new InvalidDataException();
            using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate != null);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token);
            var peer = tls.RemoteCertificate ?? throw new InvalidDataException();
            var fingerprint = Fingerprint(peer);
            var review = await VerifyChannelAsync(tls, identity.RawData, peer.GetRawCertData(), true, trusted(fingerprint), session, deadline.Token);
            var approval = await confirm(review).WaitAsync(deadline.Token);
            await WriteAsync(tls, new Consent(approval.Accept, approval.Remember), deadline.Token);
            var remote = await ReadAsync<Consent>(tls, deadline.Token);
            if (!approval.Accept || !remote.Accept) throw new OperationCanceledException("Pairing declined.");
            // Account authorization is obtained only after both users approve this TLS channel.
            var transfer = await create().WaitAsync(deadline.Token);
            try
            {
                await WriteAsync(tls, new Payload(transfer.PairingKey, Convert.ToBase64String(transfer.Bytes)), deadline.Token);
                if (!(await ReadAsync<Consent>(tls, deadline.Token)).Accept) throw new InvalidDataException();
                completed(fingerprint, approval.Remember && remote.Remember);
            }
            finally { CryptographicOperations.ZeroMemory(transfer.Bytes); }
        }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; stop.Cancel(); listener.Stop(); discovery.Dispose(); stop.Dispose(); }
    }

    internal sealed record DiscoveryInterface(IPAddress Address, IPAddress Mask);
    public static Task<IReadOnlyList<Candidate>> DiscoverAsync(CancellationToken token, IEnumerable<IPAddress>? remembered = null, Action<string>? status = null)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsLocalAddress(a.Address) && !IPAddress.IsLoopback(a.Address))
            .Select(a => new DiscoveryInterface(a.Address, a.IPv4Mask)).DistinctBy(a => a.Address).Take(16).ToList();
        return DiscoverOnInterfacesAsync(interfaces, remembered ?? [], status, token);
    }

    internal static async Task<IReadOnlyList<Candidate>> DiscoverOnInterfacesAsync(IReadOnlyList<DiscoveryInterface> interfaces,
        IEnumerable<IPAddress> remembered, Action<string>? status, CancellationToken token, bool broadcast = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var known = remembered.Where(IsLocalAddress).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct().Take(32).ToArray();
        var found = new Dictionary<string, Candidate>();
        var errors = new HashSet<SocketError>();
        var gate = new object();
        var attempts = 0; var sent = 0; var received = 0;
        var probe = Encoding.ASCII.GetBytes(Probe);
        void Error(SocketError error) { lock (gate) errors.Add(error); }
        void Report()
        {
            string text;
            lock (gate) text = $"Discovery: {attempts} interfaces, {sent} probes sent, {received} replies; socket status: {(errors.Count == 0 ? "none" : string.Join(", ", errors.OrderBy(e => e)))}.";
            status?.Invoke(text);
        }
        async Task Search(DiscoveryInterface binding)
        {
            Interlocked.Increment(ref attempts);
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            try { udp.Client.Bind(new IPEndPoint(binding.Address, 0)); }
            catch (SocketException ex) { Error(ex.SocketErrorCode); Report(); return; }
            var targets = new HashSet<IPAddress>(known);
            if (broadcast)
            {
                targets.Add(IPAddress.Broadcast);
                var bytes = binding.Address.GetAddressBytes(); var mask = binding.Mask.GetAddressBytes();
                targets.Add(new IPAddress(bytes.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
            }
            async Task SendProbes()
            {
                try
                {
                    for (var round = 0; round < 3; round++)
                    {
                        if (round > 0) await Task.Delay(2500, timeout.Token);
                        foreach (var address in targets)
                        {
                            try { await udp.SendAsync(probe, new IPEndPoint(address, DiscoveryPort), timeout.Token); Interlocked.Increment(ref sent); }
                            catch (SocketException ex) { Error(ex.SocketErrorCode); }
                        }
                        Report();
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            var sending = SendProbes();
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var packet = await udp.ReceiveAsync(timeout.Token);
                    if (!IsLocalAddress(packet.RemoteEndPoint.Address) || packet.Buffer.Length > 100) continue;
                    var parts = Encoding.ASCII.GetString(packet.Buffer).Split('|');
                    if (parts.Length != 3 || parts[0] != "AL-LAN-PAIR-1" || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535 || parts[2].Length != 32 || !parts[2].All(char.IsAsciiHexDigit)) continue;
                    Interlocked.Increment(ref received);
                    var candidate = new Candidate(packet.RemoteEndPoint.Address.ToString(), port, parts[2]);
                    lock (gate) { if (found.Count < 32) found[$"{candidate.Address}:{port}"] = candidate; }
                    Report();
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (SocketException ex) { Error(ex.SocketErrorCode); }
            finally { await sending; }
        }
        await Task.WhenAll(interfaces.Select(Search));
        token.ThrowIfCancellationRequested();
        Report();
        return found.Values.ToList();
    }

    public static async Task<Received> ReceiveAsync(Candidate host, X509Certificate2 identity, Func<string, bool> trusted,
        Func<Review, Task<Approval>> confirm, Func<MobileTransferFile, CancellationToken, Task> import, CancellationToken token)
    {
        if (!IPAddress.TryParse(host.Address, out var address) || !IsLocalAddress(address) || host.Port is < 1 or > 65535) throw new InvalidDataException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        await tcp.ConnectAsync(address, host.Port, deadline.Token);
        using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate != null);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "achievement-labs-local", ClientCertificates = new X509CertificateCollection { identity },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, deadline.Token);
        var peer = tls.RemoteCertificate ?? throw new InvalidDataException(); var fingerprint = Fingerprint(peer);
        var review = await VerifyChannelAsync(tls, peer.GetRawCertData(), identity.RawData, false, trusted(fingerprint), host.Session, deadline.Token);
        var approval = await confirm(review).WaitAsync(deadline.Token);
        await WriteAsync(tls, new Consent(approval.Accept, approval.Remember), deadline.Token);
        var remote = await ReadAsync<Consent>(tls, deadline.Token);
        if (!approval.Accept || !remote.Accept) throw new OperationCanceledException("Pairing declined.");
        var payload = await ReadAsync<Payload>(tls, deadline.Token);
        if (payload.Key?.Length != 43 || payload.File == null || payload.File.Length > 174764) throw new InvalidDataException();
        var transfer = new MobileTransferFile(Convert.FromBase64String(payload.File), payload.Key);
        try
        {
            if (transfer.Bytes.Length is 0 or > 131072) throw new InvalidDataException();
            // Import verifies both the payload's account and the server's current profile.
            await import(transfer, deadline.Token).WaitAsync(deadline.Token);
            await WriteAsync(tls, new Consent(true, approval.Remember && remote.Remember), deadline.Token);
            return new Received(transfer, fingerprint, approval.Remember && remote.Remember);
        }
        finally { CryptographicOperations.ZeroMemory(transfer.Bytes); }
    }

    // Commit both random challenges before revealing them. This prevents an active
    // intermediary choosing certificates/challenges adaptively to match a short code.
    static async Task<Review> VerifyChannelAsync(Stream stream, byte[] server, byte[] client, bool isServer,
        bool remembered, string expectedSession, CancellationToken token)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var ownCert = isServer ? server : client;
        var peerCert = isServer ? client : server;
        string Commitment(byte[] challenge, byte[] cert) => Convert.ToHexString(SHA256.HashData(challenge.Concat(cert).ToArray()));
        await WriteAsync(stream, new Hello(1, isServer ? expectedSession : "", Commitment(nonce, ownCert)), token);
        var hello = await ReadAsync<Hello>(stream, token);
        if (hello.Version != 1 || hello.Commitment?.Length != 64 || (!isServer && expectedSession.Length > 0 && hello.Session != expectedSession)) throw new InvalidDataException();
        await WriteAsync(stream, new Challenge(Convert.ToBase64String(nonce)), token);
        var challenge = await ReadAsync<Challenge>(stream, token);
        if (challenge.Nonce?.Length != 44) throw new InvalidDataException();
        var other = Convert.FromBase64String(challenge.Nonce);
        if (other.Length != 32 || Commitment(other, peerCert) != hello.Commitment) throw new InvalidDataException();
        var transcript = Encoding.ASCII.GetBytes("AchievementLabs-LAN-v1").Concat(SHA256.HashData(server)).Concat(SHA256.HashData(client))
            .Concat(isServer ? nonce : other).Concat(isServer ? other : nonce).ToArray();
        var number = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(transcript)) % 100000000;
        var code = number.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        return new Review(code[..4] + " " + code[4..], Convert.ToHexString(SHA256.HashData(peerCert)), remembered);
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            if (bytes.Length > MaxFrame) throw new InvalidDataException();
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
            await stream.WriteAsync(prefix, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token);
        var size = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (size is < 2 or > MaxFrame) throw new InvalidDataException();
        var bytes = new byte[size];
        try
        {
            await stream.ReadExactlyAsync(bytes, token);
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var names = new HashSet<string>(); foreach (var p in doc.RootElement.EnumerateObject()) if (!names.Add(p.Name)) throw new InvalidDataException();
            return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    sealed record Hello(int Version, string Session, string Commitment);
    sealed record Challenge(string Nonce);
    sealed record Consent(bool Accept, bool Remember);
    sealed record Payload(string Key, string File);
}
