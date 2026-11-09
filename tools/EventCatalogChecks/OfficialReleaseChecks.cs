using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AchievementLabs.Core;
using Newtonsoft.Json.Linq;

static class OfficialReleaseChecks
{
    sealed class Release(byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = "https://github.com/Achievement-Labs/achievement-labs/releases/download/v1.3.3/Events-Indexed.zip";
            if (request.Method != HttpMethod.Get) throw new Exception("Only fixture GETs are allowed.");
            HttpContent content = request.RequestUri!.Host == "api.github.com"
                ? new StringContent(new JArray(new JObject { ["tag_name"] = "v1.3.3", ["draft"] = false, ["prerelease"] = false,
                    ["assets"] = new JArray(new JObject { ["name"] = "Events-Indexed.zip", ["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), ["browser_download_url"] = url }) }).ToString())
                : request.RequestUri.AbsoluteUri == url ? new ByteArrayContent(archive) : throw new Exception("Unexpected fixture URL.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    public static async Task RunAsync(string directory)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "labs-official-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
                {
                    var entry = zip.CreateEntry("Events/" + Path.GetRelativePath(directory, path).Replace('\\', '/'), CompressionLevel.Fastest);
                    using var output = entry.Open(); using var input = File.OpenRead(path); input.CopyTo(output);
                }
            using var http = new HttpClient(new Release(bytes.ToArray()));
            using var bundled = new EventCatalogClient(true, officialCatalog: true);
            var expected = bundled.GetLibrarySupport();
            var catalog = new UpstreamEventCatalogue(Path.Combine(temporary, "official.json"), ["345903534"], bundledTitleIds: expected.Keys, officialCatalog: true);
            await catalog.RefreshAsync(http, true);
            if (catalog.Status.StartsWith("Could not") || catalog.TitleIds.Length != expected.Count - 1) throw new Exception(catalog.Status);
            foreach (var item in expected.Where(p => p.Key != "345903534"))
            {
                var actual = catalog.LibrarySupport()[item.Key];
                if (actual.Available != item.Value.Available || actual.FullySupported != item.Value.FullySupported
                    || actual.PartiallySupported != item.Value.PartiallySupported || actual.Mapped != item.Value.Mapped)
                    throw new Exception("Published/bundled mismatch: " + item.Key);
            }
            var restarted = new UpstreamEventCatalogue(Path.Combine(temporary, "official.json"), ["345903534"], bundledTitleIds: expected.Keys, officialCatalog: true);
            if (restarted.TitleIds.Length != catalog.TitleIds.Length) throw new Exception("Full offline cache failed to reload: " + restarted.Status);
            Console.WriteLine($"PASS: full official release archive validated through actual consumer; {expected.Count} classifications match embedded catalog; atomic cache reloads offline. No network or telemetry.");
        }
        finally { Directory.Delete(temporary, true); }
    }
}
