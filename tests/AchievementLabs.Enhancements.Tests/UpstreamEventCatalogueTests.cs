using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AchievementLabs.Core;
using Newtonsoft.Json.Linq;

public static class UpstreamEventCatalogueTests
{
    static void Assert(bool value,string message) { if(!value) throw new Exception(message); }
    sealed class Fixture : HttpMessageHandler
    {
        public byte[] Archive=[];
        public bool Fail, BadDigest, WrongLocation;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            Assert(request.RequestUri!.AbsoluteUri.StartsWith("https://api.github.com/repos/Achievement-Labs/achievement-labs/",StringComparison.Ordinal) || request.RequestUri.AbsoluteUri.StartsWith("https://github.com/Achievement-Labs/achievement-labs/releases/download/",StringComparison.Ordinal), "Event metadata and archives use the canonical repository");
            Assert(request.Method==HttpMethod.Get && !request.Headers.Contains("Authorization") && !request.Headers.Contains("tickets"),"Public read only; no tokens");
            var metadata=request.RequestUri!.AbsolutePath.EndsWith("releases/latest");
            var digest=BadDigest ? new string('0',64) : Convert.ToHexString(SHA256.HashData(Archive)).ToLowerInvariant();
            HttpContent content=metadata ? new StringContent(new JObject { ["tag_name"]="v1.0.8",["draft"]=false,["prerelease"]=false,["assets"]=new JArray(new JObject { ["name"]="Events.zip",["digest"]="sha256:"+digest,["browser_download_url"]=WrongLocation ? "https://example.com/Events.zip" : "https://github.com/Achievement-Labs/achievement-labs/releases/download/v1.0.8/Events.zip" }) }.ToString()) : new ByteArrayContent(Archive);
            return Task.FromResult(new HttpResponseMessage(Fail?HttpStatusCode.Forbidden:HttpStatusCode.OK){Content=content});
        }
    }
    static JObject Block()=>JObject.Parse("""{"Achievements":{"1":{"Id":{"ReplacementType":"Replace","Target":"REPLACEACH","Replacement":"1"}}}}""");
    static string Template(string id)=>new JObject { ["name"]="Microsoft.XboxLive.T"+id+".AchievementUnlock",["data"]=new JObject{["baseData"]=new JObject{["titleId"]=id,["serviceConfigId"]="11111111-2222-3333-4444-555555555555",["name"]="AchievementUnlock",["userId"]="REPLACEXUID",["properties"]=new JObject{["AchievementID"]="REPLACEACH"}}}}.ToString();
    static byte[] Zip(JObject data,bool unsafePath=false)
    {
        using var bytes=new MemoryStream();
        using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true)) {
            void Put(string name,string text) { using var writer=new StreamWriter(zip.CreateEntry(name,CompressionLevel.Fastest).Open());writer.Write(text); }
            Put("Events/Data.json",data.ToString()); Put("Events/99.json",Template("99")); Put("Events/101.json",Template("101")); Put("Events/102.json",Template("999"));
            if(unsafePath)Put("Events/../escape.json","{}");
        }
        return bytes.ToArray();
    }
    public static void Run()
    {
        var dir=Path.Combine(Path.GetTempPath(),"labs-zip-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try {
            var path=Path.Combine(dir,"cache.json");var data=new JObject{["99"]=Block(),["101"]=Block(),["102"]=Block(),["103"]=Block(),["SupportedTitleIDs"]=new JArray("99","101","102","103")};
            var fixture=new Fixture{Archive=Zip(data)};using var http=new HttpClient(fixture);
            var catalogue=new UpstreamEventCatalogue(path,new[]{"99","345903534"});catalogue.RefreshAsync(http,true).GetAwaiter().GetResult();
            Assert(catalogue.TitleIds.SequenceEqual(new[]{"101"}) && !catalogue.Contains("99"),"Only new structurally valid titles imported; reserved mappings protected");
            Assert(catalogue.Status.Contains("2 entries skipped"),"Wrong title and missing template reported");
            var payload=catalogue.PayloadsAsync("101","1","0",default).GetAwaiter().GetResult();
            Assert(JObject.Parse(payload.Payloads[0])["data"]!["baseData"]!["properties"]!["AchievementID"]!.ToString()=="1","Cached ordinary replacement builds offline");
            var valid=File.ReadAllText(path); Assert(((JObject)JObject.Parse(valid)["data"]!).Properties().Last().Name=="SupportedTitleIDs","Data ordering maintained");
            Assert(new UpstreamEventCatalogue(path,new[]{"99"}).Contains("101"),"Digest-based cache reload works without network");
            var calls=fixture.Calls;catalogue.RefreshAsync(http).GetAwaiter().GetResult();Assert(calls==fixture.Calls,"Hourly throttle avoids duplicate downloads");
            fixture.BadDigest=true;catalogue.RefreshAsync(http,true).GetAwaiter().GetResult();Assert(File.ReadAllText(path)==valid && catalogue.Status.Contains("checksum mismatch"),"Bad checksum cannot replace cache");
            fixture.BadDigest=false;fixture.Archive=Zip(data,true);catalogue.RefreshAsync(http,true).GetAwaiter().GetResult();Assert(File.ReadAllText(path)==valid && catalogue.Status.Contains("archive path"),"Unsafe archive paths rejected without extraction or cache mutation");
            fixture.WrongLocation=true;catalogue.RefreshAsync(http,true).GetAwaiter().GetResult();Assert(File.ReadAllText(path)==valid && catalogue.Status.Contains("archive location"),"Untrusted archive locations are still rejected");fixture.WrongLocation=false;
            fixture.Fail=true;catalogue.RefreshAsync(http,true).GetAwaiter().GetResult();Assert(File.ReadAllText(path)==valid && catalogue.Status.Contains("HTTP 403"),"HTTP error details and last valid data retained");
            JObject PackTitle(string id) => new() { ["template"] = Template(id), ["recipes"] = new JObject { ["recipes"] = new JObject { ["1"] = new JArray { new JArray { new JObject { ["target"] = "REPLACEACH", ["value"] = "1" } } } } } };
            var pack = new JObject { ["schemaVersion"] = 1, ["titles"] = new JObject { ["105"] = PackTitle("105"), ["99"] = PackTitle("999") } };
            Assert(catalogue.ImportPackAsync(pack.ToString()).GetAwaiter().GetResult() == 1, "Offline pack adds new titles and preserves reserved editions");
            Assert(new UpstreamEventCatalogue(path, new[]{"99"}).Contains("105"), "Offline imported titles survive restart");
            Assert(catalogue.ImportPackAsync(pack.ToString()).GetAwaiter().GetResult() == 0, "Repeated pack import is idempotent");
            var beforeImport = File.ReadAllText(path);
            pack["titles"]!["106"] = PackTitle("999");
            bool rejected = false;
            try { catalogue.ImportPackAsync(pack.ToString()).GetAwaiter().GetResult(); } catch(InvalidDataException) { rejected = true; }
            Assert(rejected && File.ReadAllText(path) == beforeImport && !catalogue.Contains("106"), "Bad pack identity leaves memory and disk unchanged");
            Assert(JObject.Parse(beforeImport)["data"]!.Children<JProperty>().Last().Name == "SupportedTitleIDs", "Pack import preserves catalogue ordering");
            using var cancel=new CancellationTokenSource();cancel.Cancel();calls=fixture.Calls;bool stopped=false;
            try{catalogue.RefreshAsync(http,true,cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){stopped=true;}
            Assert(stopped && calls==fixture.Calls,"Cancellation makes no requests");
            // v1.0.7's real Data.json is 78.6 MB: prove the old 64 MiB restriction is gone.
            fixture.Fail=false;data.Remove("SupportedTitleIDs");data["padding"]=new string('x',65*1024*1024);data["SupportedTitleIDs"]=new JArray("99","101","102","103");fixture.Archive=Zip(data);
            var large=new UpstreamEventCatalogue(Path.Combine(dir,"large.json"),new[]{"99"});large.RefreshAsync(http,true).GetAwaiter().GetResult();Assert(large.Contains("101"),"Data larger than 64 MiB accepted inside a bounded verified ZIP");
            if (Environment.GetEnvironmentVariable("LABS_LIVE_EVENT_SOURCE") is { Length: > 0 } source) {
                var reserved=JObject.Parse(File.ReadAllText(source)).Properties().Select(p=>p.Name).ToArray();
                using var liveHttp=new HttpClient { Timeout=TimeSpan.FromMinutes(2) };
                var live=new UpstreamEventCatalogue(Path.Combine(dir,"live.json"),reserved);
                live.RefreshAsync(liveHttp,true).GetAwaiter().GetResult();
                Assert(!live.Status.StartsWith("Could not",StringComparison.Ordinal),"Live official event source: "+live.Status);
                Console.WriteLine("LIVE PUBLIC EVENT CHECK: "+live.Status);
            }
            File.WriteAllText(path,"broken");Assert(new UpstreamEventCatalogue(path,new[]{"99"}).TitleIds.Length==0,"Corrupt cache fails closed");
        } finally {Directory.Delete(dir,true);}
        Console.WriteLine("PASS: release ZIP updates, SHA-256 verification, large catalogue, title validation, reserved mappings, path rejection, cache restart, ordering, rollback, HTTP diagnostics and cancellation. Fixture reads only.");
    }
}
