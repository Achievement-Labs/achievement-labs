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
    internal const string Repository = "https://github.com/ethanwp28/achievement-labs";
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AchievementLabs", "Updates");
    internal static Version CurrentVersion => typeof(AppUpdater).Assembly.GetName().Version!;
    private readonly HttpClient http = client ?? new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string root = directory ?? Root;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<AvailableUpdate?> CheckAsync(Version current, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ethanwp28/achievement-labs/releases/latest");
        request.Headers.UserAgent.ParseAdd("AchievementLabs/" + current);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString()!;
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var feed = assets.FirstOrDefault(x => x.GetProperty("name").GetString() == "update.json");
        if (feed.ValueKind == JsonValueKind.Undefined) return null;
        var executable = assets.Single(x => x.GetProperty("name").GetString() == "AchievementLabs.exe");
        string AssetUrl(JsonElement asset)
        {
            var url = asset.GetProperty("browser_download_url").GetString()!;
            if (!url.StartsWith(Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected update location.");
            return url;
        }
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(await http.GetStringAsync(AssetUrl(feed), ct), Json)
            ?? throw new InvalidDataException("Empty update metadata.");
        if (!Version.TryParse(manifest.Version, out var version) || version.Revision < 0 || tag != $"v{version.Major}.{version.Minor}.{version.Build}")
            throw new InvalidDataException("Invalid update version.");
        if (version <= current) return null;
        if (manifest.Bytes <= 0 || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit)
            || executable.GetProperty("size").GetInt64() != manifest.Bytes
            || executable.GetProperty("digest").GetString() != "sha256:" + manifest.Sha256.ToLowerInvariant())
            throw new InvalidDataException("Update metadata does not match the published executable. Try again later.");
        return new(manifest, AssetUrl(executable));
    }

    internal async Task<string> DownloadAsync(AvailableUpdate update, CancellationToken ct)
    {
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, "download.part");
        var target = Path.Combine(folder, "AchievementLabs.exe");
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
        if (stream.Length != manifest.Bytes || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            throw new InvalidDataException("The update is not the expected Windows executable.");
        stream.Position = 0;
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("The update checksum does not match. Your current app has not changed.");
    }

    internal static void StartInstall(string download, UpdateManifest manifest)
    {
        Verify(download, manifest);
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
