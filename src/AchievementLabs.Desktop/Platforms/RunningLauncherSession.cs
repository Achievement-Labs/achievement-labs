using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json.Linq;

namespace AchievementLabs.Desktop.Platforms;

// Like Xbox attachment, this reads only the selected launcher's private memory, with query/read rights.
// No injection, writes, debug privileges, dumps, browser-profile scanning or credential logging.
internal static class RunningLauncherSession
{
    private static readonly Regex TokenPattern = new("(?:\"(?:access_token|accessToken|ticket|ubiTicket|sessionTicket|ubiServicesTicket|upcTicket)\"\\s*:\\s*\"(?<token>[^\"\\s\\\\]{32,12000})\")|(?<token>(?:eg1~|eyJ)[A-Za-z0-9_+/.=~-]{64,12000})", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    internal static string ProcessName(string platform) => platform switch { "Epic" => "EpicGamesLauncher", "EA" => "EADesktop", "Ubisoft" => "upc", _ => throw new ArgumentException("Unknown launcher.") };
    internal static string Brand(string platform) => platform switch { "Epic" => "Epic Games Launcher", "EA" => "EA app", "Ubisoft" => "Ubisoft Connect", _ => "GOG Galaxy" };
    internal static bool IsOrbitTicket(string value)
    {
        try
        {
            var header = value.Split('.')[0].Replace('-', '+').Replace('_', '/');
            var json = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header.PadRight((header.Length + 3) / 4 * 4, '='))));
            return json["enc"] != null && json["environment"] != null && json["typ"]?.ToString() == "JWE";
        }
        catch { return false; }
    }

    internal static ConnectedPlatformAccount[] ReadCandidates(string platform, CancellationToken ct, Action<string>? diagnostic = null)
    {
        var roots = Process.GetProcessesByName(ProcessName(platform));
        if (roots.Length == 0) throw new InvalidOperationException("Open " + Brand(platform) + " and sign in, then attach again.");
        var rootIds = roots.Select(p => p.Id).ToHashSet();
        var processes = platform == "Ubisoft" ? roots.Concat(Process.GetProcessesByName("UplayWebCore")).ToArray() : roots;
        var values = new Dictionary<string, (ConnectedPlatformAccount Account, int Count)>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        long read = 0;
        try
        {
            foreach (var process in processes)
            {
                // Do not attach to another Windows session or an unrelated executable with a matching name.
                using var self = Process.GetCurrentProcess();
                if (process.SessionId != self.SessionId || !TrustedPath(platform, process.MainModule?.FileName ?? "")) continue;
                if (!IsLauncherLineage(process.Id, rootIds, ParentProcessId)) continue;
                using var handle = OpenProcess(0x1000 | 0x10, false, process.Id);
                if (handle.IsInvalid) continue;
                if (!OpenProcessToken(handle, 0x8, out var ownerToken)) continue;
                using (ownerToken)
                using (var owner = new WindowsIdentity(ownerToken.DangerousGetHandle()))
                using (var current = WindowsIdentity.GetCurrent())
                    if (owner.User != current.User) continue;
                ulong address = 0;
                var buffer = new byte[1024 * 1024 + 32768];
                try
                {
                    while (VirtualQueryEx(handle, (nuint)address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (watch.Elapsed > TimeSpan.FromSeconds(30) || read > 3L * 1024 * 1024 * 1024) break;
                        var start = (ulong)region.BaseAddress;
                        var end = start + (ulong)region.RegionSize;
                        if (end <= address) break;
                        address = end;
                        if (region.State != 0x1000 || region.Type != 0x20000 || (region.Protect & (0x100 | 0x01)) != 0 || (region.Protect & (0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80)) == 0) continue;
                        for (var position = start; position < end; position += 1024 * 1024)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (watch.Elapsed > TimeSpan.FromSeconds(30) || read > 3L * 1024 * 1024 * 1024) break;
                            var length = (int)Math.Min((ulong)buffer.Length, end - position);
                            ReadProcessMemory(handle, (nuint)position, buffer, (nuint)length, out var received);
                            var count = checked((int)received);
                            if (count <= 0) continue;
                            read += count;
                            Collect(platform, Encoding.Latin1.GetString(buffer, 0, count), values);
                            Collect(platform, Encoding.Unicode.GetString(buffer, 0, count & ~1), values);
                        }
                    }
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        diagnostic?.Invoke($"Private launcher memory read: {read / 1048576} MiB; accepted candidate formats: {values.Count}.");
        return values.Values.OrderByDescending(v => v.Count).ThenByDescending(v => v.Account.ExpiresAt).Take(16).Select(v => v.Account).ToArray();
    }

    internal static bool TrustedPath(string platform, string path)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        return (name.Equals(ProcessName(platform), StringComparison.OrdinalIgnoreCase) || (platform == "Ubisoft" && name.Equals("UplayWebCore", StringComparison.OrdinalIgnoreCase))) && platform switch
        {
            "Epic" => folder.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) || folder.Contains("EpicGames", StringComparison.OrdinalIgnoreCase),
            "EA" => folder.Contains("EA Desktop", StringComparison.OrdinalIgnoreCase),
            "Ubisoft" => folder.Contains("Ubisoft Game Launcher", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
    internal static bool IsLauncherLineage(int id, HashSet<int> roots, Func<int, int?> parent)
    {
        var seen = new HashSet<int>();
        for (var depth = 0; depth < 8 && seen.Add(id); depth++)
        {
            if (roots.Contains(id)) return true;
            var next = parent(id); if (next is not > 0) return false; id = next.Value;
        }
        return false;
    }
    private static int? ParentProcessId(int id)
    {
        using var handle = OpenProcess(0x1000, false, id);
        if (handle.IsInvalid || NtQueryInformationProcess(handle, 0, out var information, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return null;
        return checked((int)information.ParentProcessId);
    }

    private static void Collect(string platform, string text, Dictionary<string, (ConnectedPlatformAccount Account, int Count)> values)
    {
        try
        {
            foreach (Match match in TokenPattern.Matches(text))
            {
                var token = match.Groups["token"].Value;
                var namedField = match.Value.StartsWith('"');
                if (namedField)
                {
                    var field = match.Value.Split('"')[1];
                    if (platform == "EA" && !field.Equals("access_token", StringComparison.OrdinalIgnoreCase) && !field.Equals("accessToken", StringComparison.OrdinalIgnoreCase)) continue;
                    if (platform == "Ubisoft" && field.Contains("access", StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (values.TryGetValue(token, out var old)) { values[token] = (old.Account, old.Count + 1); continue; }
                if (values.Count >= 256) continue;
                var candidate = ParseCandidate(platform, token, namedField);
                if (candidate != null) values[token] = (candidate, 1);
            }
        }
        catch (RegexMatchTimeoutException) { /* Bounded search; never let a launcher page wedge attachment. */ }
    }

    internal static ConnectedPlatformAccount? ParseCandidate(string platform, string token, bool namedField = true)
    {
        if (token.Length is < 32 or > 12000 || token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return null;
        JObject? claims = null;
        try
        {
            var parts = token.Split('.');
            var encoded = parts.Length == 3 ? parts[1] : parts.Length > 1 ? parts[0] : token;
            encoded = encoded.Replace('-', '+').Replace('_', '/');
            claims = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))));
        }
        catch { }
        // Never forward recognizable tokens from a different identity provider merely because
        // the launcher happens to retain them (for example, a linked Microsoft account).
        var issuer = AccountLibraryClient.Text(claims, "iss");
        if (platform == "EA")
        {
            var host = Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) ? issuerUri.Host : issuer;
            var eaIssuer = host is "ea.com" or "origin.com" || host.EndsWith(".ea.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".origin.com", StringComparison.OrdinalIgnoreCase);
            if (issuer.Length > 0 && !eaIssuer) return null;
            if (!namedField && !eaIssuer) return null;
            if (claims?["enc"] != null && !eaIssuer) return null;
        }
        if (platform == "Ubisoft")
        {
            var host = Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) ? issuerUri.Host : issuer;
            if (issuer.Length > 0 && !host.EndsWith(".ubi.com", StringComparison.OrdinalIgnoreCase) && !host.EndsWith(".ubisoft.com", StringComparison.OrdinalIgnoreCase)) return null;
            if (!namedField && claims?["enc"] == null) return null;
        }
        DateTimeOffset? expiry = null;
        if (long.TryParse(claims?["exp"]?.ToString(), out var seconds))
        {
            try { expiry = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch { return null; }
        }
        else if (DateTimeOffset.TryParse(claims?["expiration"]?.ToString(), out var expiration)) expiry = expiration;
        if (expiry < DateTimeOffset.UtcNow.AddMinutes(1)) return null;
        var id = AccountLibraryClient.Text(claims, "userId", "account_id", "pid_id", "pid", "uid", "sub");
        // Current Connect uses encrypted tickets: their header is readable, their account identity is not.
        // Only the provider's authenticated session response may establish the user in that case.
        if (platform == "Ubisoft" && token.Length < 80) return null;
        if (platform == "Ubisoft" && !Guid.TryParse(id, out _)) id = "";
        if (platform == "Epic" && !token.StartsWith("eg1~", StringComparison.Ordinal)) return null;
        var appId = AccountLibraryClient.Text(claims, "appId");
        return new(platform, id, "", token, ExpiresAt: expiry, SessionId: AccountLibraryClient.Text(claims, "sessionId"), SessionSource: Brand(platform), AuthAppId: Guid.TryParse(appId, out _) ? appId : "");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion { public nuint BaseAddress, AllocationBase; public uint AllocationProtect; public nuint RegionSize; public uint State, Protect, Type; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation { public nint ExitStatus; public nuint PebBaseAddress, AffinityMask; public nint BasePriority; public nuint ProcessId, ParentProcessId; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, out ProcessBasicInformation information, int size, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] private static extern nuint VirtualQueryEx(SafeProcessHandle process, nuint address, out MemoryRegion info, nuint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nuint address, [Out] byte[] buffer, nuint size, out nuint read);
}
