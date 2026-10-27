using AchievementLabs.Desktop;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
var root = Path.Combine(Path.GetFullPath("artifacts"), "updater-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var bytes = new byte[] { 77, 90, 1, 2, 3, 4 };
var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var manifest = new UpdateManifest("1.0.6.1", bytes.Length, hash);
if (AppUpdater.Repository != "https://github.com/Achievement-Labs/achievement-labs") throw new Exception("Wrong release repository");
var feed = "[" + JsonSerializer.Serialize(new { draft=false, prerelease=false, tag_name="v99.0.0", assets=Array.Empty<object>() }) + "," + JsonSerializer.Serialize(new { draft=false, prerelease=false, tag_name="v1.0.6", assets=new[] {
 new { name="update.json", browser_download_url=AppUpdater.Repository+"/releases/download/v1.0.6/update.json", size=0L, digest="" },
 new { name="AchievementLabs.exe", browser_download_url=AppUpdater.Repository+"/releases/download/v1.0.6/AchievementLabs.exe", size=(long)bytes.Length, digest="sha256:"+hash }
} }) + "]";
using var updater = new AppUpdater(new HttpClient(new Handler(url => url.Contains("/releases?per_page=100",StringComparison.Ordinal) ? new StringContent(feed) : url.EndsWith("update.json") ? new StringContent(JsonSerializer.Serialize(manifest,new JsonSerializerOptions(JsonSerializerDefaults.Web))) : new ByteArrayContent(bytes))),root);
var update = await updater.CheckAsync(new Version(1,0,6,0),default) ?? throw new Exception("Revision update missing");
if (await updater.CheckAsync(new Version(1,0,6,1),default) != null) throw new Exception("Same revision offered");
var badHandler = new Handler(url => url.Contains("/releases?per_page=100",StringComparison.Ordinal) ? new StringContent(feed.Replace("sha256:"+hash,"sha256:"+new string('0',64))) : new StringContent(JsonSerializer.Serialize(manifest,new JsonSerializerOptions(JsonSerializerDefaults.Web))));
using (var mismatch = new AppUpdater(new HttpClient(badHandler),root)) {
 try { await mismatch.CheckAsync(new Version(1,0,6,0),default); throw new Exception("Mismatched publication accepted"); } catch(InvalidDataException) {}
}
var foreignFeed = feed.Replace(AppUpdater.Repository + "/releases/download/", "https://github.com/thevampirebat/achievement-labs/releases/download/", StringComparison.Ordinal);
using (var foreign = new AppUpdater(new HttpClient(new Handler(url => url.Contains("/releases?per_page=100",StringComparison.Ordinal) ? new StringContent(foreignFeed) : new StringContent("{}"))),root)) {
 try { await foreign.CheckAsync(new Version(1,0,6,0),default); throw new Exception("Old repository download accepted"); } catch(InvalidDataException) {}
}
var revisionManifest = manifest with { Version="1.0.6.2" };
var mixedFeed = feed[..^1] + "," + feed.Replace("v1.0.6","v1.0.6.2",StringComparison.Ordinal)[1..];
using (var mixed = new AppUpdater(new HttpClient(new Handler(url => url.Contains("/releases?per_page=100",StringComparison.Ordinal) ? new StringContent(mixedFeed) : new StringContent(JsonSerializer.Serialize(url.Contains("/v1.0.6.2/",StringComparison.Ordinal) ? revisionManifest : manifest,new JsonSerializerOptions(JsonSerializerDefaults.Web))))),root)) {
 var latest = await mixed.CheckAsync(new Version(1,0,6,0),default) ?? throw new Exception("Windows release hidden by another platform");
 if (latest.Manifest.Version!="1.0.6.2") throw new Exception("Highest Windows revision was not selected");
}
using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); try { await updater.DownloadAsync(update,cancelled.Token); throw new Exception("Cancellation ignored"); } catch(OperationCanceledException) {} }
var download = await updater.DownloadAsync(update,default);
var target = Path.Combine(root,"installed.exe");
var events = Path.Combine(root,"Events"); Directory.CreateDirectory(events); File.WriteAllText(Path.Combine(events,"Data.json"),"user edits");
File.WriteAllText(target,"old executable");
var backup = AppUpdater.ReplaceAndLaunch(download,target,manifest,_ => {});
if(File.ReadAllText(backup)!="old executable" || !File.ReadAllBytes(target).SequenceEqual(bytes)) throw new Exception("Replacement failed");
File.WriteAllText(target,"old executable");
try { AppUpdater.ReplaceAndLaunch(download,target,manifest,_ => throw new IOException("Startup failed")); throw new Exception("Rollback not triggered"); } catch(IOException) {}
if(File.ReadAllText(target)!="old executable") throw new Exception("Rollback failed");
File.WriteAllBytes(download,new byte[]{77,90,9,9,9,9});
try { AppUpdater.ReplaceAndLaunch(download,target,manifest,_=>{}); throw new Exception("Corruption accepted"); } catch(CryptographicException) {}
if(File.ReadAllText(target)!="old executable") throw new Exception("Corruption changed app");
using var truncated = new AppUpdater(new HttpClient(new Handler(_ => new ByteArrayContent(new byte[]{77,90}))),root);
try { await truncated.DownloadAsync(update,default); throw new Exception("Truncation accepted"); } catch(InvalidDataException) {}
using var oversized = new AppUpdater(new HttpClient(new Handler(_ => new ByteArrayContent(new byte[99]))),root);
try { await oversized.DownloadAsync(update,default); throw new Exception("Oversize accepted"); } catch(InvalidDataException) {}
if(Directory.GetFiles(root,"download.part",SearchOption.AllDirectories).Length!=0) throw new Exception("Partial files retained");
if(File.ReadAllText(Path.Combine(events,"Data.json"))!="user edits") throw new Exception("Events modified");
Console.WriteLine("PASS: canonical repository, mixed-platform releases, highest revision, foreign download rejection, revision detection, same-version rejection, download verification, replacement, backup, startup rollback, checksum/truncated/oversized rejection, partial cleanup, Events preservation.");
sealed class Handler(Func<string,HttpContent> response) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { ct.ThrowIfCancellationRequested(); if(request.Headers.Contains("Authorization") || request.Headers.Contains("tickets")) throw new Exception("Account credentials reached update requests"); if(request.RequestUri!.Host=="api.github.com" && request.RequestUri.AbsoluteUri!="https://api.github.com/repos/Achievement-Labs/achievement-labs/releases?per_page=100") throw new Exception("Wrong release API"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=response(request.RequestUri!.AbsoluteUri)}); } }
