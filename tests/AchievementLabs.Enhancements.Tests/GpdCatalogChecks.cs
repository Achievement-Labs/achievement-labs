using System.Net;
using System.Security.Cryptography;
using System.Text;
using AchievementLabs.Services.Xbox360;

// Cross-checks the C# GPD reset and catalog client against fixtures written by profile-core.js
// (node tools/xbox360/shared/core-checks.cjs --gpd-fixtures <dir>). Synthetic data only.
static class GpdCatalogChecks
{
 public static void Run(string dir)
 {
  byte[] input=File.ReadAllBytes(Path.Combine(dir,"input.gpd")),expected=File.ReadAllBytes(Path.Combine(dir,"template.gpd"));
  var reset=Xbox360GpdTemplate.Reset(input);
  if(!reset.SequenceEqual(expected))throw new Exception("C# GPD reset differs from profile-core.js");
  if(!Xbox360GpdTemplate.Reset(reset).SequenceEqual(reset))throw new Exception("GPD reset is not idempotent");
  var before=Xbox360GpdTemplate.Inspect(input);var after=Xbox360GpdTemplate.Inspect(reset);
  if(before.Unlocked!=1||after is not {Name:"Catalog game",Achievements:2,Gamerscore:40,Unlocked:0})throw new Exception("GPD inspection differs: "+before+" | "+after);
  foreach(var bad in new[]{Encoding.ASCII.GetBytes("nope"),new byte[24]})
   try{Xbox360GpdTemplate.Reset(bad);throw new Exception("Invalid GPD accepted");}catch(InvalidDataException){}

  var added=Path.Combine(dir,"added.con");
  if(!Xbox360ConSigner.VerifyFile(added))throw new Exception("Mobile add-game CON signature failed independent C# verification");
  Xbox360StfsIntegrity.VerifyFile(added);
  var packages=new Xbox360PackageService();
  if(!packages.ReadEntryBytes(added,"ABCD0001.gpd").SequenceEqual(expected))throw new Exception("Inserted GPD differs from the locked template");
  var original=Path.Combine(dir,"before.con");
  foreach(var entry in packages.InspectPackage(original).Entries.Where(e=>!e.IsDirectory))
   if(!packages.ReadEntryBytes(added,entry.Path).SequenceEqual(packages.ReadEntryBytes(original,entry.Path)))throw new Exception("Existing profile file changed: "+entry.Path);

  var catalogDir=Path.Combine(dir,"catalog-client");if(Directory.Exists(catalogDir))Directory.Delete(catalogDir,true);
  var sha=Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
  string row(string name,string hash)=>"{\"name\":\""+name+"\",\"achievements\":2,\"gamerscore\":40,\"bytes\":"+expected.Length+",\"sha256\":\""+hash+"\",\"release\":\"gpd-catalog-1\"}";
  string index="{\"version\":1,\"titles\":{\"ABCD0001\":"+row("Catalog game",sha)+",\"ABCD0002\":"+row("Tampered",new string('0',64))+"}}";
  var client=new Xbox360GpdCatalogClient(new HttpClient(new Fake(index,expected)),Path.Combine(catalogDir,"GPD"));
  var titles=client.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
  if(titles.Count!=2)throw new Exception("Catalog index parse failed");
  var path=client.DownloadAsync("ABCD0001",CancellationToken.None).GetAwaiter().GetResult();
  if(!File.ReadAllBytes(path).SequenceEqual(expected))throw new Exception("Catalog download wrote different bytes");
  try{client.DownloadAsync("ABCD0002",CancellationToken.None).GetAwaiter().GetResult();throw new Exception("Checksum mismatch accepted");}catch(InvalidDataException){}
  if(File.Exists(Path.Combine(catalogDir,"GPD","ABCD0002.gpd")))throw new Exception("Rejected GPD was written");
  var offline=new Xbox360GpdCatalogClient(new HttpClient(new Fake(null,null)),Path.Combine(catalogDir,"GPD"));
  if(offline.LoadAsync(CancellationToken.None).GetAwaiter().GetResult().Count!=2)throw new Exception("Offline catalog cache fallback failed");
  try{Xbox360GpdCatalogClient.Parse(Encoding.UTF8.GetBytes("""{"version":1,"titles":{"ABCD0001":{"bytes":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","release":"gpd-catalog/../x"}}}"""));throw new Exception("Invalid index accepted");}catch(InvalidDataException){}
  Console.WriteLine("PASS: C# GPD reset matches profile-core.js byte-for-byte; mobile add-game profile verifies independently (signature, STFS tree, inserted GPD, unchanged files); catalog client checks checksums, refuses tampered GPDs and falls back to its verified cache.");
 }
 sealed class Fake(string? index,byte[]? gpd):HttpMessageHandler
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   if(index==null)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
   var path=request.RequestUri!.AbsolutePath;
   HttpContent body=path.EndsWith("/index.json")?new StringContent(index):new ByteArrayContent(gpd!);
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=body});
  }
 }
}
