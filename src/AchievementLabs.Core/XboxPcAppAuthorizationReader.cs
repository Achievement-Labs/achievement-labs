using Memory;
using System.Runtime.InteropServices;

namespace AchievementLabs.Core;

public static class XboxPcAppAuthorizationReader
{
    private const string ProcessName = "XboxPcApp";
    private const string XauthPattern = "58 42 4C 33 2E 30 20 78 3D";

    public static async Task<string?> TryReadAsync(CancellationToken cancellationToken = default)
        => (await ReadCandidatesAsync(cancellationToken)).FirstOrDefault();

    public static async Task<string?> TryReadValidatedAsync(string? expectedXuid, bool regionOverride, CancellationToken cancellationToken = default)
    {
        foreach (var value in await ReadCandidatesAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var api = new XboxApiClient(value, regionOverride);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                var profile = await api.GetBasicProfileAsync(timeout.Token);
                var xuid = profile?.ProfileUsers.FirstOrDefault()?.Id;
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(xuid) && (expectedXuid == null || expectedXuid == xuid)) return value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* A stale cached candidate must not hide a valid newer one. */ }
        }
        return null;
    }

    private static async Task<IReadOnlyList<string>> ReadCandidatesAsync(CancellationToken cancellationToken)
    {
        var memory = new Mem();
        try
        {
            if (!memory.OpenProcess(ProcessName)) return [];
            var addresses = (await memory.AoBScan(XauthPattern, true)).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            if (addresses.Length == 0) return [];

            var candidates = addresses
                .Select(address => memory.ReadString(address.ToString("X"), length: 10000))
                .Where(value => value.StartsWith("XBL3.0 x=", StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length == 0) return [];
            return candidates
                .GroupBy(value => value, StringComparer.Ordinal)
                .Where(group => group.Count() > 3)
                .Where(group => EventTokenValidator.TryValidate(group.Key, out _, out _))
                .OrderByDescending(group => { EventTokenValidator.TryValidate(group.Key, out var expiry, out _); return expiry; })
                .ThenByDescending(group => group.Count())
                .Take(8).Select(group => group.Key).ToArray();
        }
        finally
        {
            if (memory.mProc.Handle != IntPtr.Zero) CloseHandle(memory.mProc.Handle);
            memory.mProc.Process?.Dispose();
        }
    }
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
