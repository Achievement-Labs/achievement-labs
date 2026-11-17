#if WINDOWS
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Management.Deployment;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace AchievementLabs.Desktop;

/// <summary>Installs-check, launch and token capture for Windows 8 Store games. Only processes running under
/// the game's own package identity are read (HTML/JS titles hosted by WWAHost carry it too), and only the
/// XBL2.0 assertion is kept.</summary>
public static class Windows8TokenGrab
{
    public static string LogFile => AchievementLabs.Core.AchievementLabsPaths.LocalFile(Path.Combine("logs", "token-grab.txt"));
    static void Log(string line)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!); File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine); }
        catch { /* best-effort */ }
    }

    // ---- package ----

    public static bool IsInstalled(string pfn)
    {
        if (string.IsNullOrWhiteSpace(pfn)) return false;
        try { if (new PackageManager().FindPackagesForUser(string.Empty, pfn).Any()) return true; } catch { }
        try { return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", pfn)); }
        catch { return false; }
    }

    public static async Task<bool> LaunchAsync(string pfn)
    {
        var entry = new PackageManager().FindPackagesForUser(string.Empty, pfn).FirstOrDefault()?.GetAppListEntries().FirstOrDefault();
        return entry != null && await entry.LaunchAsync();
    }

    /// <summary>Resolves the title's package family name from Title Hub.</summary>
    public static async Task<string> ResolvePfnAsync(HttpClient http, string titleId, string xuid, string authorization, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"https://titlehub.xboxlive.com/users/xuid({Uri.EscapeDataString(xuid)})/titles/batch/decoration/detail")
        { Content = new StringContent(JsonSerializer.Serialize(new { Pfns = (string?)null, TitleIds = new[] { titleId } }), Encoding.UTF8, "application/json") };
        message.Headers.TryAddWithoutValidation("Authorization", authorization);
        message.Headers.TryAddWithoutValidation("x-xbl-contract-version", "2");
        message.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        using var response = await http.SendAsync(message, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array || titles.GetArrayLength() == 0) return "";
        return titles[0].TryGetProperty("pfn", out var pfn) && pfn.ValueKind == JsonValueKind.String ? pfn.GetString() ?? "" : "";
    }

    /// <summary>Force-closes every process running under the package.</summary>
    public static void Close(string pfn)
    {
        foreach (var pid in PackageProcesses(pfn))
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
    }

    // ---- capture ----

    /// <summary>Scans the package's processes until a token appears or the timeout elapses.</summary>
    public static string? Grab(string pfn, TimeSpan timeout, CancellationToken ct)
    {
        Log($"=== grab: {pfn}, up to {timeout.TotalSeconds:0}s ===");
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var pids = PackageProcesses(pfn);
            if (pids.Count == 0) Log("no process running under the package yet");
            foreach (var pid in pids)
            {
                ct.ThrowIfCancellationRequested();
                try { if (Scan(pid, ct) is { } token) { Log($"token found in pid {pid}"); return token; } }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log($"pid {pid}: {ex.Message}"); }
            }
            if (DateTime.UtcNow >= deadline) break;
            ct.WaitHandle.WaitOne(2000);
        } while (true);
        Log("timed out, no token");
        return null;
    }

    static List<int> PackageProcesses(string pfn)
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            var pid = process.Id; process.Dispose();
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                var length = 0u;
                if (GetPackageFamilyName(handle, ref length, null) != ERROR_INSUFFICIENT_BUFFER) continue;
                var name = new StringBuilder((int)length);
                if (GetPackageFamilyName(handle, ref length, name) == 0 && string.Equals(name.ToString(), pfn, StringComparison.OrdinalIgnoreCase)) found.Add(pid);
            }
            finally { CloseHandle(handle); }
        }
        return found;
    }

    static string? Scan(int pid, CancellationToken ct)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero) { Log($"pid {pid}: VM_READ blocked ({Marshal.GetLastWin32Error()})"); return null; }
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var token = ScanProcess(handle, ct, out var scanned, out var markers);
            Log($"pid {pid}: read {scanned >> 20} MB, {markers} marker(s), {(token != null ? "TOKEN" : "no token")} in {stopwatch.ElapsedMilliseconds} ms");
            return token;
        }
        finally { CloseHandle(handle); }
    }

    // The game keeps the bare assertion resident and only prepends "XBL2.0 x=" when it builds the header.
    const string Open = "<EncryptedAssertion", Namespace = "urn:oasis:names:tc:SAML:2.0:assertion", Closing = "</EncryptedAssertion>";
    const string OpenJwt = "<jwt", ClosingJwt = "</jwt>";

    static string? ScanProcess(IntPtr handle, CancellationToken ct, out long scanned, out int markers)
    {
        // Both markers start "<" then "E"/"j", so one search for each encoding's "<" prefix covers all four.
        byte[][] needles = [Encoding.ASCII.GetBytes(Open), Encoding.Unicode.GetBytes(Open), Encoding.ASCII.GetBytes(OpenJwt), Encoding.Unicode.GetBytes(OpenJwt)];
        var buffer = new byte[8 * 1024 * 1024];
        const int overlap = 4096;
        scanned = 0; markers = 0;
        var address = 0L;
        var mbiSize = (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (VirtualQueryEx(handle, (IntPtr)address, out var region, mbiSize) == mbiSize)
        {
            ct.ThrowIfCancellationRequested();
            var baseAddress = region.BaseAddress.ToInt64(); var size = region.RegionSize.ToInt64();
            if (size <= 0) break;
            var readable = region.State == MEM_COMMIT && (region.Protect & ReadableMask) != 0 && (region.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0;
            var wanted = region.Type == MEM_PRIVATE || (region.Type == MEM_MAPPED && size <= 32 * 1024 * 1024);
            if (readable && wanted && scanned < 3L * 1024 * 1024 * 1024)
            {
                var regionEnd = baseAddress + size;
                for (var pos = baseAddress; pos < regionEnd;)
                {
                    ct.ThrowIfCancellationRequested();
                    var toRead = (int)Math.Min(buffer.Length, regionEnd - pos);
                    if (!ReadProcessMemory(handle, (IntPtr)pos, buffer, (IntPtr)toRead, out var readPtr) || (int)readPtr <= 0) break;
                    var got = (int)readPtr; scanned += got;
                    var span = buffer.AsSpan(0, got);
                    for (var off = 0; off < got;)
                    {
                        var hit = span[off..].IndexOf((byte)'<');
                        if (hit < 0) break;
                        off += hit;
                        for (var n = 0; n < needles.Length; n++)
                        {
                            if (!span[off..].StartsWith(needles[n])) continue;
                            markers++;
                            if (ReadToken(handle, pos + off, regionEnd, n % 2 == 1, out var why) is { } token) return token;
                            if (markers <= 12) Log($"  marker {(n < 2 ? "saml" : "jwt")}{(n % 2 == 1 ? "/utf16" : "")} rejected: {why}");
                        }
                        off++;
                    }
                    if (got < toRead || pos + toRead >= regionEnd) break;
                    pos += Math.Max(1, got - overlap);
                }
            }
            var next = baseAddress + size;
            if (next <= address) break;
            address = next;
        }
        return null;
    }

    /// <summary>Reads from the marker to at most the region end (a read past it would fail outright).</summary>
    static string? ReadToken(IntPtr handle, long address, long regionEnd, bool wide, out string why)
    {
        var buffer = new byte[(int)Math.Min(64 * 1024, regionEnd - address)];
        if (!ReadProcessMemory(handle, (IntPtr)address, buffer, (IntPtr)buffer.Length, out var readPtr) || (int)readPtr <= 0) { why = "read failed"; return null; }
        var got = (int)readPtr;
        return ExtractToken(wide ? Encoding.Unicode.GetString(buffer, 0, got - (got % 2)) : Encoding.ASCII.GetString(buffer, 0, got), out why);
    }

    /// <summary>Returns a complete SAML assertion or &lt;jwt&gt; token as "XBL2.0 x=…", or null.</summary>
    public static string? ExtractToken(string text) => ExtractToken(text, out _);

    /// <summary>As above; <paramref name="why"/> describes a rejection by shape only (never token content).</summary>
    public static string? ExtractToken(string text, out string why)
    {
        why = "";
        if (text.StartsWith(Open, StringComparison.Ordinal))
        {
            var ns = text.IndexOf(Namespace, StringComparison.Ordinal); var end = text.IndexOf(Closing, StringComparison.Ordinal);
            if (end > 0 && ns > 0 && ns < end)
            {
                var assertion = text[..(end + Closing.Length)];
                if (HasCipherPayload(assertion)) return "XBL2.0 x=" + assertion;
                why = $"no 200+ base64 run after CipherValue (length {assertion.Length}, longest run {LongestRun(assertion, IsBase64)})";
            }
            else why = $"incomplete (namespace {(ns > 0 ? "yes" : "no")}, closing tag {(end > 0 ? "yes" : "no")}, window {text.Length})";
        }
        else if (text.StartsWith(OpenJwt, StringComparison.Ordinal))
        {
            // Some titles put the token between <jwt v="1.0"> and </jwt>; others (TY) carry it in an attribute
            // of a self-closing <jwt v="1.0" …="…"/>. Take the whole element either way.
            var tagEnd = TagEnd(text);
            var selfClosing = tagEnd > 1 && text[tagEnd - 1] == '/';
            var end = selfClosing ? tagEnd + 1 : tagEnd > 0 ? text.IndexOf(ClosingJwt, tagEnd, StringComparison.Ordinal) : -1;
            if (end > 0)
            {
                var element = selfClosing ? text[..end] : text[..(end + ClosingJwt.Length)];
                var run = LongestRun(element, IsBase64Url);
                if (run >= 200) return "XBL2.0 x=" + element;
                why = $"{(selfClosing ? "self-closing" : "paired")} element of {element.Length} chars, longest base64url run {run}";
            }
            else why = tagEnd > 0 ? $"open tag of {tagEnd} chars but no </jwt> in {text.Length}" : $"open tag never ends in {text.Length} chars";
        }
        return null;
    }

    /// <summary>Index of the '>' ending the opening tag, skipping any inside quoted attribute values.</summary>
    static int TagEnd(string text)
    {
        var quote = '\0';
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i;
            else if (c == '<') return -1;
        }
        return -1;
    }

    // A real token carries a long base64 run after CipherValue; text that only mentions the token doesn't.
    static bool HasCipherPayload(string assertion)
    {
        const string tag = "CipherValue>";
        for (var at = assertion.IndexOf(tag, StringComparison.Ordinal); at >= 0; at = assertion.IndexOf(tag, at + 1, StringComparison.Ordinal))
            if (LongestRun(assertion.AsSpan(at + tag.Length), IsBase64, stopAtFirstBreak: true) >= 200) return true;
        return false;
    }

    static int LongestRun(ReadOnlySpan<char> text, Func<char, bool> allowed, bool stopAtFirstBreak = false)
    {
        int best = 0, run = 0;
        foreach (var c in text)
        {
            if (allowed(c)) { if (++run > best) best = run; }
            else if (stopAtFirstBreak) break;
            else run = 0;
        }
        return best;
    }

    static bool IsBase64(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=';
    static bool IsBase64Url(char c) => IsBase64(c) || c is '-' or '_' or '.';

    // ---- interop ----

    const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_VM_READ = 0x0010;
    const int MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, MEM_MAPPED = 0x40000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    const int ReadableMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public int AllocationProtect, __alignment1;
        public IntPtr RegionSize;
        public int State, Protect, Type, __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION buffer, IntPtr length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr bytesRead);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
}
#endif
