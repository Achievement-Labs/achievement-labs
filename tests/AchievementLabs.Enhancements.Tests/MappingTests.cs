using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AchievementLabs.MultiSelect;

public static class MappingTests
{
 static void Assert(bool value,string message){if(!value)throw new Exception(message);}
 sealed class Handler:HttpMessageHandler
 {
  public string[] Catalog=Enumerable.Range(1,91).Select(i=>i.ToString()).ToArray();
  public readonly List<string> SentIds=new();
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   string result;
   if(request.Method==HttpMethod.Get)result=JsonSerializer.Serialize(new{achievementIds=Catalog});
   else
   {
    using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
    SentIds.Add(json.RootElement.GetProperty("achievementId").GetString()!);
    result="{\"payloads\":[]}";
   }
   return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(result,Encoding.UTF8,"application/json")};
  }
 }
 static object? Await(object task)
 {
  ((Task)task).GetAwaiter().GetResult();return task.GetType().GetProperty("Result")?.GetValue(task);
 }
 public static void Run()
 {
  System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(_,name)=>
  {
   string path=Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("AL_TEST_ROOT")??AppContext.BaseDirectory,name.Name+".dll"));
   return File.Exists(path)?Assembly.LoadFrom(path):null;
  };
  string title=EventIdMapping.GhostsTitleId;
  bool blocked=false;try{EventIdMapping.PayloadId(title,"92");}catch(InvalidOperationException){blocked=true;}
  Assert(blocked,"Unloaded catalog must not guess a mapping");
  var assembly=Assembly.LoadFrom(Path.GetFullPath((Environment.GetEnvironmentVariable("AL_TEST_ROOT")??AppContext.BaseDirectory)+"/AchievementLabs.Core.dll"));
  var type=assembly.GetTypes().Single(t=>t.Name=="EventCatalogClient");
  var client=Activator.CreateInstance(type)!;
  var handler=new Handler();
  var http=new HttpClient(handler){BaseAddress=new Uri("https://catalog.test.invalid/")};
  type.GetField("http",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,http);
  var getTitle=type.GetMethod("GetTitleAsync")!;var getPayload=type.GetMethod("GetPayloadsAsync")!;
  object Load()=>Await(getTitle.Invoke(client,new object[]{title,CancellationToken.None})!)!;
  void Payload(string tid,string id)=>Await(getPayload.Invoke(client,new object[]{tid,id,"0",CancellationToken.None})!);
  var mapping=Load();
  var ids=(IEnumerable<string>)mapping.GetType().GetProperty("AchievementIds")!.GetValue(mapping)!;
  using var exported=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ghosts-ids.json")));
  var expected=exported.RootElement.EnumerateArray().Select(x=>x.GetProperty("id").GetString()!).ToArray();
  Assert(ids.SequenceEqual(expected),"Normalized catalog equals all 91 exported Xbox IDs");
  foreach(var id in expected)Payload(title,id);
  Assert(handler.SentIds.SequenceEqual(Enumerable.Range(1,91).Select(i=>i.ToString())),"All base and DLC requests map bijectively to catalog slots");
  handler.SentIds.Clear();Payload(title,"53");Payload(title,"54");Payload(title,"92");
  Assert(handler.SentIds.SequenceEqual(new[]{"52","53","91"}),"Pushing Ahead, Weapon Facility and Hat Trick payload IDs");
  var fake=new Model();fake.definitions["92"]=new object();fake.mappedIds=ids.ToHashSet();
  Assert(BatchPicker.IneligibleReason(fake,new Row("92","Hat Trick",true))==null,"Hat Trick retry is eligible");
  handler.Catalog=expected;Load();handler.SentIds.Clear();foreach(var id in expected)Payload(title,id);
  Assert(handler.SentIds.SequenceEqual(expected),"An upstream corrected catalog receives no offset");
  handler.SentIds.Clear();Payload("different-title","92");Assert(handler.SentIds.Single()=="92","Other games unchanged");
  handler.Catalog=new[]{"1","2","92"};blocked=false;
  try{Load();}catch(InvalidOperationException){blocked=true;}
  Assert(blocked,"Unexpected Ghosts catalog rejected");blocked=false;
  try{Payload(title,"92");}catch(TargetInvocationException ex) when(ex.InnerException is InvalidOperationException){blocked=true;}
  Assert(blocked,"Unknown catalog cannot retain an old translation");
  Console.WriteLine("PASS: actual patched catalog client, all 91 exported IDs, all 41 DLC translations, Hat Trick eligibility, upstream correction, unknown catalog rejection and unrelated titles. All HTTP requests intercepted locally; no unlock requests sent.");
 }
}
