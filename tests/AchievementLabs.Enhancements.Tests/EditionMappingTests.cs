using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AchievementLabs.MultiSelect;

public static class EditionMappingTests
{
    static void Assert(bool yes,string message){if(!yes)throw new Exception(message);}
    sealed class Handler:HttpMessageHandler
    {
        public string[] Catalog=[];
        public Dictionary<string,string[]> Payloads=new();
        public string? LastId;
        public Func<string[],string[]>? Change;
        public bool FailGet;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            // Intercept every request. There is no network transport beneath this handler.
            Assert(request.RequestUri!.Host=="catalog.test.invalid","Unexpected destination");
            object result;
            if(request.Method==HttpMethod.Get)
            {
                if(FailGet)throw new HttpRequestException("Synthetic offline response");
                result=new {achievementIds=Catalog};
            }
            else
            {
                using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                LastId=doc.RootElement.GetProperty("achievementId").GetString()!;
                var title=request.RequestUri.AbsolutePath.Split('/')[^2];
                var p=Payloads[title+"-"+LastId];
                result=new {payloads=Change==null?p:Change(p)};
            }
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(result),Encoding.UTF8,"application/json")};
        }
    }
    static object Await(object task)
    {
        ((Task)task).GetAwaiter().GetResult();return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
    static void Reject(Action action,string message)
    {
        try{action();}catch(Exception e) when(e is InvalidOperationException or HttpRequestException || e is TargetInvocationException {InnerException:InvalidOperationException}){return;}
        throw new Exception("Expected rejection: "+message);
    }
    public static void Run()
    {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(_,name)=>
        {
            var p=Path.GetFullPath((Environment.GetEnvironmentVariable("AL_TEST_ROOT")??AppContext.BaseDirectory)+"/"+name.Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;
        };
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"edition-fixtures.json")));
        var rows=fixture.RootElement.GetProperty("Rows").EnumerateArray().ToArray();
        var payloads=fixture.RootElement.GetProperty("Payloads").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.EnumerateArray().Select(v=>v.GetString()!).ToArray());
        var type=Assembly.LoadFrom(Path.GetFullPath((Environment.GetEnvironmentVariable("AL_TEST_ROOT")??AppContext.BaseDirectory)+"/AchievementLabs.Core.dll")).GetTypes().Single(t=>t.Name=="EventCatalogClient");
        var client=Activator.CreateInstance(type)!;
        var handler=new Handler{Payloads=payloads};
        type.GetField("http",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,new HttpClient(handler){BaseAddress=new Uri("https://catalog.test.invalid/")});
        object Load(string t)=>Await(type.GetMethod("GetTitleAsync")!.Invoke(client,[t,CancellationToken.None])!);
        void Payload(string t,string a)=>Await(type.GetMethod("GetPayloadsAsync")!.Invoke(client,[t,a,"0",CancellationToken.None])!);
        string[] Catalog(string t)=>payloads.Keys.Where(k=>k.StartsWith(t+"-")).Select(k=>k.Split('-')[1]).ToArray();
        string[] Ids(object value)=>(string[])value.GetType().GetProperty("AchievementIds")!.GetValue(value)!;
        int tested=0;
        foreach(var (console,pc) in new[]{("796798669","754791096"),("2154930","649881449")})
        {
            Reject(()=>Payload(console,"2"),"No catalog loaded");
            var consoleRows=rows.Where(r=>r.GetProperty("Title ID").GetString()==console).ToArray();
            var pcRows=rows.Where(r=>r.GetProperty("Title ID").GetString()==pc).ToDictionary(r=>r.GetProperty("Achievement").GetString()!);
            handler.Catalog=Catalog(console);
            var queue = new AchievementLabs.Desktop.Workflows.AutoUnlockerViewModel(
                new AchievementLabs.Desktop.Workflows.NativeNotices(_ => { }),
                new AchievementLabs.Desktop.Workflows.NativeAccountContext())
                { EventCatalog = (AchievementLabs.Core.EventCatalogClient)client };
            typeof(AchievementLabs.Desktop.Workflows.AutoUnlockerViewModel)
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(queue, new AchievementLabs.Models.AutoUnlockState { TitleId = console, IsEventBased = true });
            queue.PrepareEventMappingsAsync(CancellationToken.None).GetAwaiter().GetResult();
            Payload(console, "2"); // Restored queue works without opening the manual achievement tab.
            var normalized=Ids(Load(console));
            Assert(normalized.ToHashSet().SetEquals(consoleRows.Select(r=>r.GetProperty("Achievement ID").GetString()!)),"Every Xbox achievement exposed");
            foreach(var row in consoleRows)
            {
                var name=row.GetProperty("Achievement").GetString()!;
                if(name=="Cracking Skulls")name="Cracking Skuls";
                var expected=pcRows[name].GetProperty("Achievement ID").GetString()!;
                Payload(console,row.GetProperty("Achievement ID").GetString()!);
                Assert(handler.LastId==expected,"Name-based cross-edition request: "+name);
                tested++;
            }
            Reject(()=>Payload(console,"9999"),"Unknown achievement");
            // An upstream correction must use actual Xbox IDs, without double remapping.
            var originals=handler.Payloads;var corrected=new Dictionary<string,string[]>(originals);
            foreach(var row in consoleRows)
            {
                var name=row.GetProperty("Achievement").GetString()!;if(name=="Cracking Skulls")name="Cracking Skuls";
                corrected[console+"-"+row.GetProperty("Achievement ID").GetString()]=originals[console+"-"+pcRows[name].GetProperty("Achievement ID").GetString()];
            }
            handler.Payloads=corrected;handler.Catalog=normalized;Load(console);
            foreach(var id in normalized){Payload(console,id);Assert(handler.LastId==id,"Corrected upstream IDs stay intact");}
            handler.Payloads=originals;handler.Catalog=Catalog(console);Load(console);
            handler.FailGet=true;Reject(()=>Load(console),"Failed refresh");handler.FailGet=false;
            Reject(()=>Payload(console,"2"),"Failed refresh invalidates previous mapping");
        }
        const string esc="125965001";
        handler.Catalog=Catalog(esc);var es=Ids(Load(esc));Assert(es.Length==14,"Exactly 14 reviewed Escapists mappings");
        var meanings=new Dictionary<string,(string Slot,string Event,int Count)>{
            ["5"]=("15","ObtainedBonusItem",24),["6"]=("10","NoKilledSurvivor",1),
            ["7"]=("5","RickWeaponKill",100),["8"]=("18","RickMeleeKill",50),
            ["9"]=("19","WeaponObtained",5),["10"]=("6","WalkerAteMeat",1),
            ["11"]=("2","UsedTheRadio",1),["12"]=("1","ItemEquiped",2),
            ["13"]=("17","MoneySpendAtVendor",1),["14"]=("4","ItemCrafted",30),
            ["15"]=("12","EscapeUnderTenDays",1),["16"]=("13","NoThreatAllDay",1),
            ["18"]=("9","ObtainedBonusItem",12),["20"]=("7","TrophyKill",1)};
        foreach(var (id,meaning) in meanings)
        {
            Payload(esc,id);Assert(handler.LastId==meaning.Slot,"Escapists slot "+id);
            var p=payloads[esc+"-"+meaning.Slot];Assert(p.Length==meaning.Count,"Event count for "+id);
            Assert(p.All(s=>JsonDocument.Parse(s).RootElement.GetProperty("name").GetString()!.EndsWith("."+meaning.Event)),"Descriptive event for "+id);tested++;
        }
        foreach(var id in new[]{"1","2","3","4","17","21","22","23","24","25"})
        {Assert(!es.Contains(id),"Unverified achievement excluded");Reject(()=>Payload(esc,id),"Unverified achievement blocked");}
        // Changes with the SAME catalog IDs are still caught before returning an event.
        foreach(var change in new Func<string[],string[]>[]{
            p=>[], p=>p.Concat(p).ToArray(),p=>p.Select(s=>s.Replace("RickMeleeKill","RickWeaponKill")).ToArray(),
            p=>p.Select(s=>s.Replace("125965001","754791096")).ToArray(),
            p=>p.Select(s=>s.Replace("d5850100-5d2f-49cb-9444-8b0d078212c9","wrong-scid")).ToArray(),
            p=>["{}"]})
        {
            handler.Catalog=Catalog(esc);Load(esc);handler.Change=change;
            Reject(()=>Payload(esc,"8"),"Changed payload rejected");handler.Change=null;
            Reject(()=>Payload(esc,"8"),"Changed payload invalidates mapping");
        }
        handler.Catalog=["1","2"];Reject(()=>Load(esc),"Unknown catalog shape");
        Assert(EventIdMapping.PayloadId("754791096","39")=="39" && EventIdMapping.PayloadId("649881449","4")=="4","PC editions unchanged");
        Console.WriteLine($"PASS: {tested} actual patched-client remaps; corrected catalogs; 10 unverified Escapists IDs blocked; payload changes, missing/extra events, wrong title/SCID and failed refresh rejected. All HTTP intercepted locally.");
    }
}
