using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AchievementLabs.Desktop;

internal sealed record UpdateManifest(string Version, long Bytes, string Sha256);
internal sealed record AvailableUpdate(UpdateManifest Manifest, string DownloadUrl);
internal sealed record UpdatePlan(int ParentPid, long ParentStartTicks, string Target, string Download, UpdateManifest Manifest);

internal sealed class AppUpdater(HttpClient? client = null, string? directory = null) : IDisposable
{
    internal const string Repository = "https://github.com/Achievement-Labs/achievement-labs";
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievementLabs", "Updates");
    internal static Version CurrentVersion => typeof(AppUpdater).Assembly.GetName().Version!;
    private readonly HttpClient http = client ?? new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string root = directory ?? Root;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Version? TagCeiling(string tag)
    {
        if (!tag.StartsWith("v", StringComparison.Ordinal) || !Version.TryParse(tag[1..].Replace("-fork.", ".", StringComparison.Ordinal), out var version) || version.Build < 0) return null;
        return version.Revision < 0 ? new(version.Major, version.Minor, version.Build, int.MaxValue) : version;
    }
    internal async Task<AvailableUpdate?> CheckAsync(Version current, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux()) return null;
#if LABS_MACOS
        if (OperatingSystem.IsMacOS()) return null;
#endif
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Achievement-Labs/achievement-labs/releases?per_page=100");
        request.Headers.UserAgent.ParseAdd("AchievementLabs/" + current);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 100) throw new InvalidDataException("Invalid release list.");
        var candidates = document.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean())
            .Select(r => new { Release = r, Tag = r.GetProperty("tag_name").GetString()! })
            .Select(r => new { r.Release, r.Tag, Ceiling = TagCeiling(r.Tag) })
            .Where(r => r.Ceiling != null).OrderByDescending(r => r.Ceiling);
        AvailableUpdate? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Ceiling! <= (best is null ? current : Version.Parse(best.Manifest.Version))) continue;
            var assets = candidate.Release.GetProperty("assets").EnumerateArray().ToArray();
            var feed = assets.FirstOrDefault(x => x.GetProperty("name").GetString() == "update.json");
            var executable = assets.FirstOrDefault(x => x.GetProperty("name").GetString() == "AchievementLabs.exe");
            if (feed.ValueKind == JsonValueKind.Undefined || executable.ValueKind == JsonValueKind.Undefined) continue;
            string AssetUrl(JsonElement asset)
            {
                var url = asset.GetProperty("browser_download_url").GetString()!;
                var expected = Repository + "/releases/download/" + Uri.EscapeDataString(candidate.Tag) + "/" + Uri.EscapeDataString(asset.GetProperty("name").GetString()!);
                if (url != expected) throw new InvalidDataException("Unexpected update location.");
                return url;
            }
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(await http.GetStringAsync(AssetUrl(feed), ct), Json)
                ?? throw new InvalidDataException("Empty update metadata.");
            if (!Version.TryParse(manifest.Version, out var version) || version.Revision < 0 ||
                !(candidate.Tag == $"v{version.Major}.{version.Minor}.{version.Build}" || candidate.Tag == "v" + version || candidate.Tag == $"v{version.Major}.{version.Minor}.{version.Build}-fork.{version.Revision}"))
                throw new InvalidDataException("Invalid update version.");
            if (version <= current || best != null && version <= Version.Parse(best.Manifest.Version)) continue;
            if (manifest.Bytes <= 0 || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit)
                || executable.GetProperty("size").GetInt64() != manifest.Bytes
                || executable.GetProperty("digest").GetString() != "sha256:" + manifest.Sha256.ToLowerInvariant())
                throw new InvalidDataException("Update metadata does not match the published executable. Try again later.");
            best = new(manifest, AssetUrl(executable));
        }
        return best;
    }

    internal async Task<string> DownloadAsync(AvailableUpdate update, CancellationToken ct)
    {
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, "download.part");
        var target = Path.Combine(folder, OperatingSystem.IsMacOS() ? "AchievementLabs-macOS-arm64.zip" : "AchievementLabs.exe");
        try
        {
            using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > update.Manifest.Bytes) throw new InvalidDataException("Update download exceeds its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            Verify(partial, update.Manifest);
            File.Move(partial, target);
            return target;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    internal static void Verify(string file, UpdateManifest manifest)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length != manifest.Bytes || stream.ReadByte() != (OperatingSystem.IsMacOS() ? 'P' : 'M') || stream.ReadByte() != (OperatingSystem.IsMacOS() ? 'K' : 'Z'))
            throw new InvalidDataException("The update is not the expected platform package.");
        stream.Position = 0;
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The update checksum does not match. Your current app has not changed.");
    }

    internal static void StartInstall(string download, UpdateManifest manifest)
    {
        if (OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Download Linux preview updates manually.");
        Verify(download, manifest);
#if LABS_MACOS
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac preview updates are installed manually from fork releases.");
#endif
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("Application path unavailable.");
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use the published Windows EXE to install an update.");
        // Confirm the app's location is writable before asking it to exit.
        var probe = Path.Combine(Path.GetDirectoryName(target)!, ".labs-update-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        var folder = Path.GetDirectoryName(download)!;
        var helper = Path.Combine(folder, "Updater.exe");
        File.Copy(target, helper, false);
        var parent = Process.GetCurrentProcess();
        var plan = Path.Combine(folder, "plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new UpdatePlan(parent.Id, parent.StartTime.ToUniversalTime().Ticks, target, download, manifest), Json));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(plan);
        _ = Process.Start(start) ?? throw new IOException("The update helper could not start.");
    }

    internal static string ReplaceAndLaunch(string download, string target, UpdateManifest manifest, Action<string> launch)
    {
        Verify(download, manifest);
        var backup = target + ".previous-" + Guid.NewGuid().ToString("N");
        var incoming = target + ".incoming-" + Guid.NewGuid().ToString("N");
        File.Copy(download, incoming, false);
        try
        {
            File.Move(target, backup);
            try { File.Move(incoming, target); launch(target); }
            catch
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(backup, target);
                throw;
            }
            return backup;
        }
        finally { if (File.Exists(incoming)) File.Delete(incoming); }
    }

    internal static int Apply(string planFile)
    {
        try
        {
            if (!IsUpdatePath(planFile)) throw new InvalidDataException("Invalid update plan location.");
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planFile), Json)!;
            if (!IsUpdatePath(plan.Download) || !Path.IsPathFullyQualified(plan.Target) || !plan.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update paths.");
            try
            {
                using var parent = Process.GetProcessById(plan.ParentPid);
                if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartTicks && !parent.WaitForExit(120000))
                    throw new IOException("The current app did not exit. It has not been replaced.");
            }
            catch (ArgumentException) { } // Parent already exited.
            var health = Path.Combine(Path.GetDirectoryName(planFile)!, "ready");
            if (File.Exists(health)) File.Delete(health);
            try
            {
                var backup = ReplaceAndLaunch(plan.Download, plan.Target, plan.Manifest, path =>
                {
                    var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
                    start.ArgumentList.Add("--update-health"); start.ArgumentList.Add(health);
                    using var child = Process.Start(start) ?? throw new IOException("Updated app could not start.");
                    var timeout = Stopwatch.StartNew();
                    while (!File.Exists(health) && !child.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(45)) Thread.Sleep(100);
                    if (!File.Exists(health))
                    {
                        if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); }
                        throw new IOException("Updated app did not finish startup; the previous EXE was restored.");
                    }
                });
                File.WriteAllText(Path.Combine(Root, "last-result.txt"), "Updated to " + plan.Manifest.Version + ". Previous EXE: " + backup);
            }
            catch
            {
                Process.Start(new ProcessStartInfo(plan.Target) { UseShellExecute = true });
                throw;
            }
            return 0;
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "last-result.txt"), "Update failed: " + error.Message);
            return 1;
        }
    }

    internal static bool IsUpdatePath(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public void Dispose() => http.Dispose();
}
