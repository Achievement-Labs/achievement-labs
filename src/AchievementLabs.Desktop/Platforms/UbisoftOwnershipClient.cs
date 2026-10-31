using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AchievementLabs.Desktop.Platforms;

// Original read-only client for the launcher's TLS ownership service. Sends version/auth/open/initialize only.
// Wire references: docs/account-library-integration.md. Never requests game tokens, activations or presence.
internal static partial class UbisoftOwnershipClient
{
    internal static Action<string>? AchievementMetadataDiagnostic;
    internal const string AppId = "f68a4bb5-608a-4ff2-8123-be8ef797e0a6";
    internal static async Task<PlatformGameRow[]> ReadAsync(ConnectedPlatformAccount session, CancellationToken ct)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync("dmx.upc.ubisoft.com", 443, ct);
        using var tls = new SslStream(socket.GetStream(), false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "dmx.upc.ubisoft.com" }, ct);
        await WriteFrame(tls, Proto.Message(2, Proto.Message(4, Proto.Number(1, LauncherVersion()))), ct);
        var token = Proto.Message(session.SessionKind == "UbisoftOrbit" ? 1 : 3, Encoding.UTF8.GetBytes(session.AccessToken));
        var auth = Proto.Join(Proto.Message(1, token), Proto.Message(2, Encoding.UTF8.GetBytes("uplay_pc")), Proto.Number(4, 0));
        await WriteFrame(tls, Proto.Message(1, Proto.Join(Proto.Number(1, 1), Proto.Message(2, auth))), ct);
        var authentication = await Response(tls, 1, ct);
        if (Proto.Get(Proto.Get(authentication, 2).Bytes, 1).Number != 1) throw new InvalidOperationException("Ubisoft ownership service rejected the launcher session.");
        await WriteFrame(tls, Proto.Message(1, Proto.Join(Proto.Number(1, 2), Proto.Message(3, Proto.Message(2, Encoding.UTF8.GetBytes("ownership_service"))))), ct);
        var opened = Proto.Get((await Response(tls, 2, ct)), 3).Bytes;
        if (Proto.Get(opened, 2).Number != 1) throw new InvalidOperationException("Ubisoft ownership service was unavailable.");
        var connection = Proto.Get(opened, 1).Number;
        var initialize = Proto.Message(1, Proto.Join(Proto.Number(1, 1), Proto.Message(2, Proto.Number(2, 0))));
        var inner = Frame(initialize);
        await WriteFrame(tls, Proto.Message(2, Proto.Message(1, Proto.Join(Proto.Number(1, connection), Proto.Message(2, inner)))), ct);
        using var responseBytes = new MemoryStream();
        for (var i = 0; i < 128; i++)
        {
            var frame = await ReadFrame(tls, ct);
            var push = Proto.Get(frame, 2).Bytes;
            CheckPush(push);
            var data = Proto.Get(push, 1).Bytes;
            if (data.Length == 0 || Proto.Get(data, 1).Number != connection) continue;
            var bytes = Proto.Get(data, 2).Bytes;
            await responseBytes.WriteAsync(bytes, ct);
            if (responseBytes.Length < 4) continue;
            var buffer = responseBytes.GetBuffer();
            var length = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            if (length > 32 * 1024 * 1024) throw new InvalidDataException("Ubisoft ownership response was too large.");
            if (responseBytes.Length >= length + 4) return ParseResponse(buffer.AsSpan(4, (int)length).ToArray(), session.AccountId);
        }
        throw new InvalidDataException("Ubisoft ownership response did not complete.");
    }
    private static uint LauncherVersion()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = registry.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher");
            var root = key?.GetValue("InstallDir") as string;
            var path = root == null ? "" : Path.Combine(root, "version.txt");
            if (File.Exists(path) && uint.TryParse(File.ReadAllText(path).Trim(), out var version)) return version;
        }
        return 13393;
    }
    private static void CheckPush(byte[] push)
    {
        if (Proto.Fields(push).Any(f => f.Tag == 5)) throw new InvalidOperationException("Ubisoft requires a newer launcher protocol version. Update Ubisoft Connect and retry.");
        if (Proto.Fields(push).Any(f => f.Tag == 2)) throw new InvalidOperationException("Ubisoft closed the ownership connection.");
    }
    private static async Task<byte[]> Response(Stream stream, ulong requestId, CancellationToken ct)
    {
        for (var i = 0; i < 32; i++)
        {
            var frame = await ReadFrame(stream, ct);
            CheckPush(Proto.Get(frame, 2).Bytes);
            var response = Proto.Get(frame, 1).Bytes;
            if (response.Length > 0 && Proto.Get(response, 1).Number == requestId) return response;
        }
        throw new InvalidDataException("Ubisoft did not answer the ownership request.");
    }
    private static byte[] Frame(byte[] payload) { var framed = new byte[payload.Length + 4]; BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length); payload.CopyTo(framed, 4); return framed; }
    private static async Task WriteFrame(Stream stream, byte[] payload, CancellationToken ct) => await stream.WriteAsync(Frame(payload), ct);
    private static async Task<byte[]> ReadFrame(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > 32 * 1024 * 1024) throw new InvalidDataException("Invalid Ubisoft frame length.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct); return bytes;
    }
    internal static PlatformGameRow[] ParseResponse(byte[] response, string accountId)
    {
        var initialized = Proto.Get(Proto.Get(response, 1).Bytes, 2).Bytes;
        if (Proto.Get(initialized, 1).Number != 1) throw new InvalidDataException("Ubisoft did not return a complete owned library.");
        var games = new List<PlatformGameRow>();
        foreach (var row in Proto.Fields(Proto.Get(initialized, 2).Bytes).Where(f => f.Tag == 1))
        {
            var fields = Proto.Fields(row.Bytes);
            if (fields.Any(f => f.Tag == 20 && f.Number == 0) || fields.Any(f => f.Tag == 8 && f.Number is not (0 or 2))) continue;
            var config = Encoding.UTF8.GetString(Proto.Get(row.Bytes, 17).Bytes);
            if (!config.Contains("start_game:", StringComparison.Ordinal)) continue;
            var id = Proto.Get(row.Bytes, 19).Number;
            if (id == 0) id = Proto.Get(row.Bytes, 1).Number;
            if (id == 0) continue;
            var root = config.Split("localizations:", 2, StringSplitOptions.None)[0];
            var achievementBlock = Regex.Match(root, @"(?im)^.*achiev.*(?:\r?\n[ \t]+.*){0,4}", RegexOptions.None, TimeSpan.FromSeconds(1)).Value;
            var bundle = Regex.Match(achievementBlock, @"\b\d+_[a-f0-9]{32}\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Value;
            if (AchievementMetadataDiagnostic != null && achievementBlock.Length > 0)
            {
                var keys = Regex.Matches(achievementBlock, @"(?m)^\s*([a-zA-Z_]+):").Select(m => m.Groups[1].Value).Distinct();
                AchievementMetadataDiagnostic("Ubisoft achievement configuration fields: " + string.Join(",", keys) + "; bundle reference present: " + (bundle.Length > 0));
            }
            string Resolve(string key)
            {
                var value = YamlValue(root, key);
                var localized = YamlValue(config[(root.Length)..], value);
                return localized.Length > 0 ? localized : value;
            }
            var name = Resolve("name");
            if (name.Length == 0 || name is "GAMENAME" or "gamename" or "NAME") name = YamlValue(config, "GAMENAME");
            var art = Resolve("thumb_image");
            if (art.Length > 0 && !Uri.TryCreate(art, UriKind.Absolute, out _)) art = "https://ubistatic3-a.akamaihd.net/orbit/uplay_launcher_3_0/assets/" + art.TrimStart('/');
            games.Add(new("Ubisoft", id.ToString(), name.Length > 0 ? name : "Ubisoft " + id, ImageUrl: AccountLibraryClient.NormalizeImage(art), LaunchUri: "uplay://launch/" + id + "/0", Source: "Ubisoft account ownership service", AccountId: accountId, ServiceId: Encoding.UTF8.GetString(Proto.Get(row.Bytes, 30).Bytes), AchievementCatalogKey: bundle, AchievementProductId: Proto.Get(row.Bytes, 1).Number.ToString()));
        }
        return games.GroupBy(g => g.Id).Select(g => g.First()).ToArray();
    }
    private static string YamlValue(string text, string key)
    {
        if (key.Length == 0) return "";
        var match = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + @":\s*([^\r\n]+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value.Trim().Trim('\'', '"') : "";
    }
}

// Bounded wire reader: unknown fields are skipped; credential-bearing ownership fields are never materialized as text.
internal static class Proto
{
    internal sealed record Field(int Tag, ulong Number, byte[] Bytes);
    internal static List<Field> Fields(byte[] data)
    {
        var fields = new List<Field>(); var position = 0;
        ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64 && position < data.Length; shift += 7) { var next = data[position++]; value |= (ulong)(next & 127) << shift; if ((next & 128) == 0) return value; }
            throw new InvalidDataException("Invalid Ubisoft wire integer.");
        }
        while (position < data.Length)
        {
            if (fields.Count >= 100000) throw new InvalidDataException("Ubisoft response contained too many fields.");
            var key = Varint(); var tag = checked((int)(key >> 3));
            if (tag == 0) throw new InvalidDataException("Invalid Ubisoft wire tag.");
            if ((key & 7) == 0) fields.Add(new(tag, Varint(), []));
            else
            {
                var length = (key & 7) switch { 2 => checked((int)Varint()), 1 => 8, 5 => 4, _ => throw new InvalidDataException("Unsupported Ubisoft wire type.") };
                if (length < 0 || length > data.Length - position) throw new InvalidDataException("Truncated Ubisoft response.");
                fields.Add(new(tag, 0, data.AsSpan(position, length).ToArray())); position += length;
            }
        }
        return fields;
    }
    internal static Field Get(byte[] data, int tag) => Fields(data).FirstOrDefault(f => f.Tag == tag) ?? new(tag, 0, []);
    private static byte[] Varint(ulong value) { var bytes = new List<byte>(); do { var next = (byte)(value & 127); value >>= 7; bytes.Add((byte)(next | (value > 0 ? 128 : 0))); } while (value > 0); return bytes.ToArray(); }
    internal static byte[] Number(int tag, ulong value) => Join(Varint((ulong)tag << 3), Varint(value));
    internal static byte[] Message(int tag, byte[] value) => Join(Varint(((ulong)tag << 3) | 2), Varint((ulong)value.Length), value);
    internal static byte[] Join(params byte[][] values) => values.SelectMany(v => v).ToArray();
}
