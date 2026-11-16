using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Memory;
using Newtonsoft.Json;

namespace AchievementLabs.Core;

public static class XboxPcAppAuthorizationReader
{
    public sealed record ValidatedSession(string Authorization, BasicProfile Profile, int ProcessId);

    // The signed-in token migrates to the full-trust host across app updates; scan both.
    private static readonly string[] ProcessNames = ["XboxPcApp", "XboxPcAppCE", "XboxPcAppFT"];

    private const string AuthorizationPrefix = "XBL3.0 x=";

    private const string XauthPattern = "58 42 4C 33 2E 30 20 78 3D";

    public static int[] FindProcessIds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<int>();
        }
        var ids = new List<int>();
        foreach (var name in ProcessNames)
            foreach (var process in Process.GetProcessesByName(name))
                using (process) ids.Add(process.Id);
        return ids.ToArray();
    }

    public static async Task<string?> TryReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var processId in FindProcessIds())
        {
            var candidates = await ReadCandidatesAsync(processId, cancellationToken);
            if (candidates.Count > 0)
            {
                return candidates[0];
            }
        }
        return null;
    }

    public static async Task<string?> TryReadValidatedAsync(string? expectedXuid, bool regionOverride, CancellationToken cancellationToken = default)
    {
        return (await TryReadValidatedSessionAsync(expectedXuid, regionOverride, cancellationToken))?.Authorization;
    }

    public static async Task<ValidatedSession?> TryReadValidatedSessionAsync(string? expectedXuid, bool regionOverride, CancellationToken cancellationToken = default, Action<string>? report = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var processId in FindProcessIds())
        {
            var validatedSession = await ValidateProcessAsync(processId, expectedXuid, regionOverride, cancellationToken, report);
            if (validatedSession != null)
            {
                return validatedSession;
            }
        }
        return null;
    }

    private static async Task<ValidatedSession?> ValidateProcessAsync(int processId, string? expectedXuid, bool regionOverride, CancellationToken cancellationToken, Action<string>? report)
    {
        foreach (string value in await ReadCandidatesAsync(processId, cancellationToken, report))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using XboxApiClient api = new XboxApiClient(value, regionOverride);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6L));
            try
            {
                var profile = await api.GetBasicProfileAsync(timeout.Token);
                var xuid = profile?.ProfileUsers.FirstOrDefault()?.Id;
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(xuid) && (expectedXuid == null || expectedXuid == xuid))
                {
                    return new ValidatedSession(value, profile!, processId);
                }
                report?.Invoke("Xbox returned a profile without a matching account.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } statusCode)
            {
                report?.Invoke($"Xbox rejected the profile check (HTTP {(int)statusCode}).");
            }
            catch (HttpRequestException ex)
            {
                report?.Invoke($"The Xbox profile request failed ({ex.HttpRequestError}).");
            }
            catch (FormatException)
            {
                report?.Invoke("The Xbox session data could not be used as a request header.");
            }
            catch (JsonException)
            {
                report?.Invoke("Xbox returned an unreadable profile response.");
            }
            catch (OperationCanceledException)
            {
                report?.Invoke("The Xbox profile check timed out.");
            }
            catch
            {
                report?.Invoke("The Xbox profile check could not complete.");
            }
        }
        return null;
    }

    private static async Task<IReadOnlyList<string>> ReadCandidatesAsync(int processId, CancellationToken cancellationToken, Action<string>? report = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mem memory = new Mem();
        try
        {
            if (!memory.OpenProcess(processId, out string _, readOnly: true))
            {
                report?.Invoke("Windows could not open the Xbox app process for reading.");
                return Array.Empty<string>();
            }
            report?.Invoke("The Xbox app opened, but no usable session was found.");
            var addresses = (await memory.AoBScan(XauthPattern, writable: true)).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return SelectCandidates(addresses.Select(address => memory.ReadString(address.ToString("X"), length: 10000)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            report?.Invoke("The Xbox app process could not be read.");
            return Array.Empty<string>();
        }
        finally
        {
            Proc mProc = memory.mProc;
            if (mProc != null)
            {
                if (mProc.Handle != IntPtr.Zero)
                {
                    CloseHandle(mProc.Handle);
                }
                mProc.Process?.Dispose();
            }
        }
    }

    // Prefer the established repeated-copy heuristic, but retain valid single-copy sessions.
    internal static IReadOnlyList<string> SelectCandidates(IEnumerable<string> values)
        => values.Select(ExtractAuthorization).OfType<string>()
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => EventTokenValidator.TryValidate(group.Key, out _, out _))
            .OrderByDescending(group => group.Count() > 3)
            .ThenByDescending(group => { EventTokenValidator.TryValidate(group.Key, out var expiry, out _); return expiry; })
            .ThenByDescending(group => group.Count())
            .Take(8).Select(group => group.Key).ToArray();

    internal static string? ExtractAuthorization(string value)
    {
        if (!value.StartsWith(AuthorizationPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        int separator = value.IndexOf(';', AuthorizationPrefix.Length);
        if (separator <= AuthorizationPrefix.Length)
        {
            return null;
        }
        for (int i = AuthorizationPrefix.Length; i < separator; i++)
        {
            char c = value[i];
            if (c < '!' || c > '~')
            {
                return null;
            }
        }
        int j;
        for (j = separator + 1; j < value.Length; j++)
        {
            char c = value[j];
            if (c < '!' || c > '~' || c == '"' || c == ';')
            {
                break;
            }
        }
        if (j <= separator + 1)
        {
            return null;
        }
        return value.Substring(0, j);
    }

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
