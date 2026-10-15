using AchievementLabs.Core;
using System.Text.Json;
public static class DishonoredMappingTests
{
 public static void Run(EventCatalogClient client)
 {
  var expected=Enumerable.Range(4,50).Concat(Enumerable.Range(57,30)).Select(i=>i.ToString()).ToHashSet();
  var ids=client.GetTitleAsync("1100121502",default).GetAwaiter().GetResult().AchievementIds;
  if(ids.Length!=80 || !expected.SetEquals(ids))throw new Exception("All 80 Dishonored Xbox identities must be preserved.");
  foreach(var (id,criterion) in new Dictionary<string,string>{["8"]="FleshAndSteel",["9"]="WallOfDarkness",["13"]="Corruptor",["23"]="Profiteer",["57"]="DLC05MrsPilsenRemorse",["61"]="DLC05ByMyHandOnly",["65"]="DLC05TimeManager",["80"]="DLC07Judgment"})
  {
   var response=client.GetPayloadsAsync("1100121502",id,"0",default).GetAwaiter().GetResult();
   using var doc=JsonDocument.Parse(response.Payloads.Single());
   if(doc.RootElement.GetProperty("data").GetProperty("baseData").GetProperty("properties").GetProperty("AchievementName").GetString()!=criterion)
    throw new Exception("Dishonored approved criterion mismatch: "+id);
  }
  Console.WriteLine("PASS: all 80 approved Dishonored IDs and eight exact criteria. No live submissions.");
 }
}
