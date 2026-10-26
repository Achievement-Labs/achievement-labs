using System.Security.Cryptography;
using System.Text.Json;
using AchievementLabs.Services.Xbox360;

static class Mobile360Checks
{
 public static void Run(string original,string edited,string expectation)
 {
  var hash=SHA256.HashData(File.ReadAllBytes(original));
  using var doc=JsonDocument.Parse(File.ReadAllText(expectation));
  string id=doc.RootElement.GetProperty("titleId").GetString()!;
  uint achievement=doc.RootElement.GetProperty("achievementId").GetUInt32();
  if(!Xbox360ConSigner.VerifyFile(edited))throw new Exception("Mobile CON signature failed independent C# verification");
  Xbox360StfsIntegrity.VerifyFile(edited);
  var reader=new Xbox360ProfileReader(new Xbox360PackageService());
  var before=reader.ReadAchievements(original,id);var after=reader.ReadAchievements(edited,id);
  var a=after.Single(x=>x.Id==achievement);
  if((a.Flags&0x30000)!=0x20000 || a.UnlockedAt!=null)throw new Exception("Mobile offline unlock flags differ");
  if(!before.Where(x=>x.Id!=achievement).SequenceEqual(after.Where(x=>x.Id!=achievement)))throw new Exception("Mobile changed unselected achievement");
  var gamesBefore=reader.ReadGames(original);var gamesAfter=reader.ReadGames(edited);
  foreach(var g in gamesBefore){var b=gamesAfter.Single(x=>x.TitleId==g.TitleId);if(b.UnlockedCount!=g.UnlockedCount+(g.TitleId==id?1:0)||b.GamerscoreUnlocked!=g.GamerscoreUnlocked+(g.TitleId==id?a.Gamerscore:0))throw new Exception("Mobile dashboard totals differ");}
  if(!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(original))))throw new Exception("Original changed");
  Console.WriteLine("PASS: independent C# verifies mobile CON signature, complete active STFS tree, offline flags, all dashboard counts, unchanged unselected achievements and original profile.");
 }
}
