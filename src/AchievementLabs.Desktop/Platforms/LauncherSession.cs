using System.Diagnostics;
using Microsoft.Win32;

namespace AchievementLabs.Desktop.Platforms;

// Explicit attachment only. GOG uses its registry session; other providers use their selected launcher process.
internal static class LauncherSession
{
    internal static async Task<int> CheckAchievementsAsync(string platform, string output)
    {
        var report = new List<string>();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            ConnectedPlatformAccount account;
            try { account = await AttachAsync(platform, deadline.Token); }
            catch (InvalidOperationException)
            {
                var saved = ConnectedPlatformAccount.Load(platform);
                if (saved == null) throw;
                account = saved;
                report.Add("Using the app's existing saved connection; launcher attachment was unavailable.");
            }
            using var client = new AccountLibraryClient();
            if (platform == "Ubisoft") UbisoftOwnershipClient.AchievementMetadataDiagnostic = report.Add;
            var library = await client.ReadAsync(account, deadline.Token);
            var loaded = 0;
            var read = 0;
            foreach (var game in library.Games.OrderByDescending(g => System.Text.RegularExpressions.Regex.IsMatch(g.Name, "Dead Island|Plague Tale|Hogwarts|Control|Callisto|Gloomhaven", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            {
                try
                {
                    var achievements = await client.ReadAchievementsAsync(account, game, deadline.Token);
                    if (achievements.Complete || achievements.Rows.Length > 0) read++;
                    report.Add($"{game.Name}: {achievements.Rows.Length} rows; {achievements.Rows.Count(a => a.Unlocked == true)} unlocked; {achievements.Rows.Count(a => a.Unlocked == false)} locked; {achievements.Rows.Count(a => a.Unlocked == null)} unknown; {achievements.Rows.Count(a => a.ImageUrl.Length > 0)} icon URLs. {achievements.Status}");
                    if (achievements.Rows.Length > 0) loaded++;
                    if (loaded >= 2) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { report.Add($"{game.Name}: read failed ({error.GetType().Name}" + (error is HttpRequestException h && h.StatusCode.HasValue ? " HTTP " + (int)h.StatusCode : "") + ")."); if (error is HttpRequestException denied && denied.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized) break; }
            }
            var passed = loaded > 0 || (read == library.Games.Length && read > 0);
            File.WriteAllLines(output, new[] { passed ? $"PASS: live {platform} achievement responses verified; {loaded} titles with rows; {read} catalogs read. No unlocks, game launches or saved credentials." : $"FAIL: no {platform} achievement rows retrieved." }.Concat(report));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllLines(output, new[] { $"FAIL: {platform} achievement check ({error.GetType().Name}). " + (error is InvalidOperationException ? error.Message : "No credentials included.") }.Concat(report)); return 1;
        }
        finally { UbisoftOwnershipClient.AchievementMetadataDiagnostic = null; }
    }
    internal static async Task<ConnectedPlatformAccount> AttachAsync(string platform, CancellationToken ct, Action<string>? diagnostic = null)
    {
        using var client = new AccountLibraryClient();
        if (platform == "GOG") return await client.AttachGalaxyAsync(Read(platform), ct);
        var scanDiagnostics = new List<string>();
        var candidates = await Task.Run(() => RunningLauncherSession.ReadCandidates(platform, ct, scanDiagnostics.Add), ct);
        foreach (var line in scanDiagnostics) diagnostic?.Invoke(line);
        diagnostic?.Invoke($"Candidate sessions: {candidates.Length}.");
        return await ValidateCandidatesAsync(candidates, client, ct, diagnostic);
    }

    internal static async Task<ConnectedPlatformAccount> ValidateCandidatesAsync(ConnectedPlatformAccount[] candidates, AccountLibraryClient client, CancellationToken ct, Action<string>? diagnostic = null)
    {
        var verified = new List<ConnectedPlatformAccount>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                var session = await client.VerifyLauncherCandidateAsync(candidate, timeout.Token);
                if (session.AccountId.Length > 0) { verified.Add(session); diagnostic?.Invoke("Candidate account verified."); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { diagnostic?.Invoke("Candidate rejected: " + error.GetType().Name + (error is HttpRequestException h && h.StatusCode.HasValue ? " HTTP " + (int)h.StatusCode : "") + "."); }
        }
        if (candidates.Any(a => a.Platform == "Ubisoft") && !verified.Any(a => a.AuthAppId == UbisoftOwnershipClient.AppId))
        {
            foreach (var orbit in candidates.Where(c => RunningLauncherSession.IsOrbitTicket(c.AccessToken)).Take(2))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var native = orbit with { AccountId = "", LauncherOrbitToken = orbit.AccessToken, SessionKind = "UbisoftOrbit", AuthAppId = UbisoftOwnershipClient.AppId };
                    var identity = await UbisoftOwnershipClient.ReadNativeAchievementStateAsync(native, timeout.Token);
                    if (identity is { } info)
                    {
                        verified.Add(native with { AccountId = info.AccountId, DisplayName = "Ubisoft Connect account" });
                        diagnostic?.Invoke("Native Ubisoft achievement account verified.");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { diagnostic?.Invoke("Native Ubisoft session rejected: " + error.GetType().Name + "."); }
            }
        }
        if (verified.Select(a => a.AccountId).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new InvalidOperationException("More than one signed-in account was found in the launcher. Restart it with the desired account and attach again.");
        var selected = verified.OrderByDescending(a => a.Platform == "Ubisoft" && a.AuthAppId == UbisoftOwnershipClient.AppId).FirstOrDefault() ?? throw new InvalidOperationException("No usable signed-in launcher session was verified. Open the launcher's library and retry attachment.");
        return selected.Platform == "Ubisoft" ? selected with { LauncherOrbitToken = candidates.FirstOrDefault(c => RunningLauncherSession.IsOrbitTicket(c.AccessToken))?.AccessToken ?? "" } : selected;
    }

    internal static async Task<int> CheckAsync(string platform, string output)
    {
        var diagnostics = new List<string>();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var session = await AttachAsync(platform, deadline.Token, diagnostics.Add);
            diagnostics.Add("Launcher account verified; fetching owned library.");
            using var client = new AccountLibraryClient();
            var library = await client.ReadAsync(session, deadline.Token);
            var sample = library.Games.Where(g => g.ImageUrl.Length > 0).Select(g => g.ImageUrl).Distinct().Take(3).ToArray();
            var loaded = 0;
            using var images = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
            foreach (var url in sample)
            {
                try
                {
                    var image = await images.GetByteArrayAsync(url, deadline.Token);
                    if (image.Length > 12 && ((image[0] == 0xff && image[1] == 0xd8) || (image[0] == 0x89 && image[1] == 0x50) || (System.Text.Encoding.ASCII.GetString(image, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(image, 8, 4) == "WEBP"))) loaded++;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw; }
                catch { }
            }
            File.WriteAllText(output, $"PASS: {platform} running session verified; {library.Games.Length} owned titles; {library.Games.Count(g => g.ImageUrl.Length > 0)} cover URLs; {loaded}/{sample.Length} sample cover downloads valid. No credentials saved, achievement writes or game launches.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(output, $"FAIL: {platform} launcher check ({error.GetType().Name}" + (error is HttpRequestException h && h.StatusCode.HasValue ? " HTTP " + (int)h.StatusCode : "") + "). " + (error is InvalidOperationException ? error.Message : "No credentials included in this report.") + Environment.NewLine + string.Join(Environment.NewLine, diagnostics));
            return 1;
        }
    }
    // Explicit read-only live diagnostic; no saved sessions, account IDs, tokens or game lists in the report.
    internal static async Task<int> CheckGalaxyAsync(string output)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var launcher = Read("GOG");
            using var client = new AccountLibraryClient();
            var account = await client.AttachGalaxyAsync(launcher, deadline.Token);
            var result = await client.ReadAsync(account, deadline.Token);
            File.WriteAllText(output, $"PASS: live Galaxy session verified; {result.Games.Length} owned titles; {result.Games.Count(g => g.ImageUrl.Length > 0)} cover URLs. No session saved, launcher files changed, games launched or achievement writes performed.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(output, "FAIL: Galaxy attachment/library check (" + error.GetType().Name + "). No credentials included in this report.");
            return 1;
        }
    }
    internal static ConnectedPlatformAccount Read(string platform)
    {
        if (platform != "GOG") throw new ArgumentException("The Galaxy registry reader only supports GOG.");
        var processes = Process.GetProcessesByName("GalaxyClient");
        try
        {
            if (processes.Length == 0) throw new InvalidOperationException("Open GOG Galaxy and sign in, then attach again.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\GOG.com\Galaxy", false);
        using var settings = root?.OpenSubKey("settings", false);
        return FromGalaxyValues(root?.GetValue("refreshToken") as string,
            settings?.GetValue("userId")?.ToString(), settings?.GetValue("username") as string);
    }

    internal static ConnectedPlatformAccount FromGalaxyValues(string? refreshToken, string? userId, string? username)
    {
        // Reject unsupported encrypted/binary formats instead of guessing at their contents.
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 16384 || refreshToken.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(userId) || userId.Length > 24 || !userId.All(char.IsAsciiDigit))
            throw new InvalidOperationException("Galaxy's signed-in session is unavailable. Finish signing in to Galaxy, or use browser sign-in.");
        return new("GOG", userId, string.IsNullOrWhiteSpace(username) ? userId : username, "", refreshToken, SessionSource: "GOG Galaxy");
    }
}
