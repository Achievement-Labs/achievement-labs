using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace AchievementLabs.Desktop.Platforms;

internal static partial class UbisoftOwnershipClient
{
    // ach_frontend receives only AuthenticateReq and ReadAchievementsReq, never WriteAchievementsReq.
    internal static async Task<Dictionary<string, Dictionary<string, string>>?> ReadAchievementStatesAsync(ConnectedPlatformAccount account, CancellationToken ct)
        => (await ReadNativeAchievementStateAsync(account, ct))?.States;
    internal static async Task<(string AccountId, Dictionary<string, Dictionary<string, string>> States)?> ReadNativeAchievementStateAsync(ConnectedPlatformAccount account, CancellationToken ct)
    {
        if (account.LauncherOrbitToken.Length == 0) return null;
        using var socket = new TcpClient(); await socket.ConnectAsync("dmx.upc.ubisoft.com", 443, ct);
        using var tls = new SslStream(socket.GetStream(), false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "dmx.upc.ubisoft.com" }, ct);
        await WriteFrame(tls, Proto.Message(2, Proto.Message(4, Proto.Number(1, LauncherVersion()))), ct);
        var auth = Proto.Join(Proto.Message(1, Proto.Message(account.SessionKind == "UbisoftOrbit" ? 1 : 3, Encoding.UTF8.GetBytes(account.AccessToken))), Proto.Message(2, Encoding.UTF8.GetBytes("uplay_pc")), Proto.Number(4, 0));
        await WriteFrame(tls, Proto.Message(1, Proto.Join(Proto.Number(1, 1), Proto.Message(2, auth))), ct);
        if (Proto.Get(Proto.Get(await Response(tls, 1, ct), 2).Bytes, 1).Number != 1) return null;
        await WriteFrame(tls, Proto.Message(1, Proto.Join(Proto.Number(1, 2), Proto.Message(3, Proto.Message(2, Encoding.UTF8.GetBytes("ach_frontend"))))), ct);
        var opened = Proto.Get(await Response(tls, 2, ct), 3).Bytes;
        if (Proto.Get(opened, 2).Number != 1) return null;
        var connection = Proto.Get(opened, 1).Number;
        async Task<byte[]> Call(byte[] request)
        {
            await WriteFrame(tls, Proto.Message(2, Proto.Message(1, Proto.Join(Proto.Number(1, connection), Proto.Message(2, Frame(request))))), ct);
            using var collected = new MemoryStream();
            for (var frame = 0; frame < 128; frame++)
            {
                var response = await ReadFrame(tls, ct);
                var push = Proto.Get(response, 2).Bytes; CheckPush(push);
                var data = Proto.Get(push, 1).Bytes;
                if (data.Length == 0 || Proto.Get(data, 1).Number != connection) continue;
                var bytes = Proto.Get(data, 2).Bytes; await collected.WriteAsync(bytes, ct);
                if (collected.Length < 4) continue;
                var buffer = collected.GetBuffer(); var size = BinaryPrimitives.ReadUInt32BigEndian(buffer);
                if (size > 32 * 1024 * 1024) throw new InvalidDataException("Ubisoft achievement response was too large.");
                if (collected.Length >= size + 4) return buffer.AsSpan(4, (int)size).ToArray();
            }
            throw new InvalidDataException("Ubisoft achievement response did not complete.");
        }
        var authenticated = await Call(Proto.Join(Proto.Number(1, 1), Proto.Message(2, Proto.Message(1, Encoding.UTF8.GetBytes(account.LauncherOrbitToken)))));
        if (Proto.Get(authenticated, 1).Number != 1 || Proto.Get(Proto.Get(authenticated, 2).Bytes, 1).Number != 1) return null;
        var state = await Call(Proto.Join(Proto.Number(1, 2), Proto.Message(3, account.AccountId.Length == 0 ? [] : Proto.Message(1, Encoding.UTF8.GetBytes(account.AccountId)))));
        if (Proto.Get(state, 1).Number != 2) throw new InvalidDataException("Ubisoft achievement request mismatch.");
        var payload = Proto.Get(state, 3).Bytes;
        var userId = Encoding.UTF8.GetString(Proto.Get(payload, 1).Bytes);
        if (!Guid.TryParse(userId, out _)) throw new InvalidDataException("Ubisoft did not identify the native achievement account.");
        return (userId, ParseAchievementStates(payload, account.AccountId.Length > 0 ? account.AccountId : userId));
    }
    internal static Dictionary<string, Dictionary<string, string>> ParseAchievementStates(byte[] state, string account)
    {
        if (Encoding.UTF8.GetString(Proto.Get(state, 1).Bytes) != account) throw new InvalidDataException("Ubisoft achievement account mismatch.");
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var receivedProducts = 0; var pcProducts = 0;
        foreach (var row in Proto.Fields(Proto.Get(state, 2).Bytes).Where(f => f.Tag == 1))
        {
            var product = Proto.Get(row.Bytes, 1).Bytes;
            receivedProducts++;
            var platform = Encoding.UTF8.GetString(Proto.Get(product, 2).Bytes);
            if (platform is not ("PC" or "pc" or "uplay" or "PCWIN")) continue;
            pcProducts++;
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in Proto.Fields(Proto.Get(row.Bytes, 2).Bytes).Where(f => f.Tag == 1))
            {
                var id = Proto.Get(item.Bytes, 1).Number;
                var timestamp = Proto.Get(item.Bytes, 2).Number;
                if (!Proto.Fields(item.Bytes).Any(f => f.Tag == 1)) continue;
                entries[id.ToString()] = timestamp is > 0 and < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds((long)timestamp).ToString("O") : "";
            }
            foreach (var key in new[] { Encoding.UTF8.GetString(Proto.Get(product, 1).Bytes), Encoding.UTF8.GetString(Proto.Get(product, 3).Bytes) }.Where(k => k.Length > 0)) result[key] = entries;
        }
        if (receivedProducts > 0 && pcProducts == 0) throw new InvalidDataException("Ubisoft player achievement platforms could not be identified as PC.");
        return result;
    }
}
