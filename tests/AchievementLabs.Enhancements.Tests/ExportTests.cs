using AchievementLabs.MultiSelect;
using System.Net;
using System.Net.Http;
using System.Text.Json;

static class ExportTests
{
 static void Check(bool test,string message){if(!test)throw new Exception(message);}
 sealed class Handler:HttpMessageHandler
 {
  public List<string> Requests=new();public Func<HttpRequestMessage,HttpResponseMessage> Reply=null!;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {Check(request.Method==HttpMethod.Get,"Exporter must be GET-only");Requests.Add(request.RequestUri!.ToString());return Task.FromResult(Reply(request));}
 }
 static HttpResponseMessage Json(string value,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(value)};
 static string Title(string id,string platform="XboxOne")=>$$$"""{"titleId":"{{{id}}}","name":"Game {{{id}}}","devices":["{{{platform}}}"],"achievement":{"totalGamerscore":1000}}""";
 static string Achievement(string title,string id)=>$$$"""{"id":"{{{id}}}","name":"A, \"quoted\"","titleAssociations":[{"id":"{{{title}}}"}],"progressState":"Achieved","description":"first\nsecond","rewards":[{"type":"Gamerscore","value":"10"}],"progression":{"timeUnlocked":"2026-01-01T00:00:00Z"}}""";
 public static void Run()=>RunAsync().GetAwaiter().GetResult();
 static async Task RunAsync()
 {
  string folder=Path.Combine(Path.GetTempPath(),"AchievementExport-tests-"+Guid.NewGuid());Directory.CreateDirectory(folder);
  try
  {
   bool fail=true;var h=new Handler();
   h.Reply=r=>
   {
    string u=r.RequestUri!.ToString();
    if(u.Contains("titlehub"))return Json("{\"titles\":["+Title("1")+","+Title("2","Xbox360")+"]}");
    if(u.Contains("titleId=2"))
    {
      Check(r.Headers.GetValues("x-xbl-contract-version").Single()=="1","Legacy progress contract");
      return fail?Json("{}",HttpStatusCode.BadRequest):Json("{\"achievements\":[{\"id\":5,\"titleId\":2,\"name\":\"Legacy\",\"gamerscore\":20,\"timeUnlocked\":\"2026-02-01T00:00:00Z\"}]}");
    }
    Check(r.Headers.GetValues("x-xbl-contract-version").Single()=="4","Modern contract");
    return u.Contains("continuationToken=")?Json("{\"achievements\":["+Achievement("1","3")+"]}"):Json("{\"achievements\":["+Achievement("1","1")+"],\"pagingInfo\":{\"continuationToken\":\"page+2\"}}");
   };
   using var http=new HttpClient(h);var engine=new AchievementExport(http,"account1",delay:(_,_)=>Task.CompletedTask);
   var progress=new List<AchievementExport.Progress>();var result=await engine.Run(folder,false,progress.Add,CancellationToken.None);
   Check(result.Saved==1 && result.Failed==1,"Successful and failed titles separated");
   string cache=Path.Combine(result.Folder,"titles");Check(Directory.GetFiles(cache,"*.json").Length==1,"Failure never cached as complete");
   string csv=File.ReadAllText(Path.Combine(result.Folder,"all-achievements.csv"));Check(csv.Contains("\"A, \"\"quoted\"\"\""),"Quotes escaped");Check(csv.Contains("\"first\nsecond\""),"Newlines retained");
   Check(progress.Last().Done==2 && progress.Last().Total==2,"Progress counts failed attempts");
   h.Requests.Clear();fail=false;result=await engine.Run(folder,false,_=>{},CancellationToken.None);
   Check(result.Skipped==1 && result.Saved==1 && result.Failed==0,"Resume skips successes and retries failures");Check(!h.Requests.Any(u=>u.Contains("titleId=1")),"No requests for cached titles");
   result=await engine.Run(folder,true,_=>{},CancellationToken.None);Check(result.Saved==2 && result.Skipped==0,"Explicit refresh reads all titles");
   File.WriteAllText(Path.Combine(cache,"1.json"),"corrupt");result=await engine.Run(folder,false,_=>{},CancellationToken.None);Check(result.Saved==1,"Corrupt checkpoint reread");
   using var stop=new CancellationTokenSource();result=await engine.Run(folder,true,p=>{if(p.Saved==1)stop.Cancel();},stop.Token);
   Check(result.Paused && result.Saved==1,"Pause checkpoints completed title");Check(File.Exists(Path.Combine(result.Folder,"all-achievements.csv")),"Pause writes combined CSV");
   var other=new AchievementExport(http,"account2",delay:(_,_)=>Task.CompletedTask);var different=await other.Run(folder,false,_=>{},CancellationToken.None);Check(different.Skipped==0 && different.Folder!=result.Folder,"Accounts do not share checkpoints");
   h.Reply=_=>Json("{\"achievements\":["+Achievement("999","1")+"]}");
   try{await engine.Achievements(new("1","x","XboxOne",1000),CancellationToken.None);throw new Exception("Wrong Title ID accepted");}catch(InvalidDataException){}
   h.Reply=_=>Json("{\"achievements\":[]}");
   try{await engine.Achievements(new("1","x","XboxOne",1000),CancellationToken.None);throw new Exception("Unexpected empty list accepted");}catch(InvalidDataException){}
   Check((await engine.Achievements(new("1","x","XboxOne",0),CancellationToken.None)).Length==0,"Valid zero-achievement title");
   h.Reply=_=>Json("{\"achievements\":[],\"pagingInfo\":{\"continuationToken\":\"same\"}}");
   try{await engine.Achievements(new("1","x","XboxOne",0),CancellationToken.None);throw new Exception("Repeated token accepted");}catch(InvalidDataException){}
   int retries=0;h.Reply=_=>++retries==1?Json("{}",HttpStatusCode.TooManyRequests):Json("{\"achievements\":[]}");
   await engine.Achievements(new("1","x","XboxOne",0),CancellationToken.None);Check(retries==2,"429 retried");
   h.Reply=_=>Json("{}",HttpStatusCode.Unauthorized);
   try{await engine.Run(folder,true,_=>{},CancellationToken.None);throw new Exception("Authentication failure ignored");}catch(UnauthorizedAccessException){}
   Check(File.ReadAllText(Path.Combine(result.Folder,"all-achievements.csv")).Contains("Legacy"),"Auth failure preserves previous data");
   // Definition-only legacy responses must be overlaid by the earned list, including paginated offline unlocks.
   int earnedPages=0;
   h.Reply=r=>
   {
    Check(r.Headers.GetValues("x-xbl-contract-version").Single()=="1","Both legacy reads use contract 1");
    if(r.RequestUri!.AbsolutePath.EndsWith("titleachievements"))return Json("""{"achievements":[{"id":69,"titleId":1297287434,"name":"First","gamerscore":10,"unlocked":false,"timeUnlocked":"2002-11-15T00:00:00Z"},{"id":70,"titleId":1297287434,"name":"Second","gamerscore":20,"unlocked":false},{"id":71,"titleId":1297287434,"name":"Third","gamerscore":30,"unlocked":false}]}""");
    return ++earnedPages==1?Json("""{"achievements":[{"id":69,"titleId":1297287434,"unlocked":true,"unlockedOnline":false,"timeUnlocked":"2002-11-15T00:00:00Z"}],"pagingInfo":{"continuationToken":"offline+2"}}"""):
      Json("""{"achievements":[{"id":70,"titleId":1297287434,"unlocked":true,"unlockedOnline":true,"timeUnlocked":"2026-01-01T00:00:00Z"}]}""");
   };
   var legacyRows=await engine.Achievements(new("1297287434","Fable III","Xbox360",0),CancellationToken.None);
   Check(legacyRows.Count(r=>r.Status=="Unlocked")==2 && legacyRows.Single(r=>r.Id=="71").Status=="Locked","Earned IDs overlay definitions without shifting or inventing unlocks");
   Check(earnedPages==2 && legacyRows.All(r=>r.LegacyProgressVerified),"Paginated offline unlocks are verified without requiring timestamps");
   h.Reply=r=>r.RequestUri!.AbsolutePath.EndsWith("titleachievements")?Json("""{"achievements":[{"id":69,"titleId":1297287434,"name":"First","unlocked":false}]}"""):Json("{}",HttpStatusCode.Unauthorized);
   try{await engine.Achievements(new("1297287434","Fable III","Xbox360",0),CancellationToken.None);throw new Exception("Failed earned read accepted as all locked");}catch(UnauthorizedAccessException){}
   h.Reply=r=>r.RequestUri!.AbsolutePath.EndsWith("titleachievements")?Json("""{"achievements":[{"id":69,"titleId":1297287434,"name":"First","unlocked":false}]}"""):Json("""{"achievements":[{"id":69,"titleId":1297287382,"unlocked":true}]}""");
   try{await engine.Achievements(new("1297287434","Fable III","Xbox360",0),CancellationToken.None);throw new Exception("Wrong edition earned list accepted");}catch(InvalidDataException){}
   int pages=0;h.Reply=_=>Json(pages++==0?"{\"titles\":["+Title("1")+"],\"pagingInfo\":{\"continuationToken\":\"next\"}}":"{\"titles\":["+Title("2")+"]}");
   Check((await engine.Games(CancellationToken.None)).Length==2,"Game history paginated");
   Console.WriteLine("PASS: read-only bulk export, title matching, modern/legacy routes, both paginations, progress, CSV escaping, resume, refresh, corrupt checkpoints, pause, account isolation, wrong-title/empty-response rejection, rate-limit retry and auth failure preservation.");
  }
  finally{Directory.Delete(folder,true);}
 }
}
