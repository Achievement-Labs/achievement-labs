using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AchievementLabs.Core;

static class LibraryCacheTests
{
    static void Check(bool condition,string message) { if(!condition)throw new Exception(message); }
    sealed class Fixture : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            var response=new HttpResponseMessage(request.Method==HttpMethod.Get ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromMinutes(7));
            return Task.FromResult(response);
        }
    }
    public static async Task RunAsync()
    {
        AppContext.SetSwitch("AchievementLabs.OfflineChecks",true);
        var root=Path.Combine(Path.GetTempPath(),"labs-library-cache-"+Guid.NewGuid().ToString("N"));
        try
        {
            var cache=new AccountLibraryCache(root);
            var titles=new TitlesList { Xuid="11",AchievementSummary=new() { MythicsUnlocked=908 },Titles=[new() {
                TitleId="7",Name="Legacy fixture",Devices=["Windows8"],Achievement=new() { CurrentAchievements=43,TotalAchievements=0,IsMythic=true },
                Stats=new { authorization="NEVER-CACHE-TOKEN" } }] };
            var profile=new BasicProfile { ProfileUsers=[new() { Id="11",Settings=[new() {Id="Gamertag",Value="Fixture"},new() {Id="Authorization",Value="NEVER-CACHE-TOKEN"}] }] };
            await cache.SaveAsync("11",titles,profile,default);
            var saved=await cache.LoadAsync("11",default);
            Check(saved?.Fresh==true && saved.Mythics==908,"Fresh account snapshot and account Mythics retained");
            var restored=saved!.ToTitles();
            Check(restored.Titles[0].Achievement?.TotalAchievements==0 && restored.Titles[0].Achievement?.BaseComplete==true,"Missing totals stay unknown while explicit Mythic survives");
            Check(await cache.LoadAsync("22",default)==null,"Another account cannot load this snapshot");
            var file=Directory.GetFiles(root).Single();var json=File.ReadAllText(file);
            Check(!json.Contains("NEVER-CACHE-TOKEN") && !json.Contains("Authorization") && !json.Contains("Stats"),"Cache stores only allowlisted display fields");
            try { await cache.SaveAsync("22",titles,profile,default);throw new Exception("Wrong owner accepted"); } catch(InvalidDataException) { }
            await cache.SaveAsync("11",titles,null,default);
            Check((await cache.LoadAsync("11",default))?.Profile.GetValueOrDefault("Gamertag")=="Fixture","Library refresh retains cached profile labels");
            saved=await cache.LoadAsync("11",default);
            File.WriteAllText(file,JsonSerializer.Serialize(saved! with {CheckedAt=DateTimeOffset.UtcNow.AddMinutes(-20)}));
            Check((await cache.LoadAsync("11",default))?.Fresh==false,"Stale data is available for labelled network-failure fallback");
            File.WriteAllText(file,JsonSerializer.Serialize(saved! with {Account="wrong-account"}));
            Check(await cache.LoadAsync("11",default)==null,"Tampered account identity rejected");
            File.WriteAllText(file,"corrupt");
            Check(await cache.LoadAsync("11",default)==null,"Corrupt snapshot does not block sign-in");
            await cache.SaveAsync("11",titles,profile,default);var original=File.ReadAllText(file);
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try { await cache.SaveAsync("11",titles,profile,cancelled.Token);throw new Exception("Cancellation ignored"); } catch(OperationCanceledException) { }
            Check(File.ReadAllText(file)==original,"Cancelled save preserves previous snapshot");
            var fixture=new Fixture();using var http=new HttpClient(new XboxReadThrottle(fixture));
            var uri=new Uri("https://cache-fixture.xboxlive.com/users/me");
            for(int attempt=0;attempt<2;attempt++)
                try { await http.GetAsync(uri);throw new Exception("Rate limit not surfaced"); } catch(HttpRequestException error) when(error.StatusCode==HttpStatusCode.TooManyRequests) { }
            Check(fixture.Calls==1 && XboxReadThrottle.RetryAfter(uri.Host)>DateTimeOffset.UtcNow.AddMinutes(6),"Retry-After suppresses peer reads without retrying Xbox");
            using var result=await http.PostAsync(uri,new StringContent("synthetic fixture only"));
            Check(result.IsSuccessStatusCode && fixture.Calls==2,"GET policy does not intercept or replay write requests");
            Console.WriteLine("PASS: account isolation, snapshot freshness/fallback, unknown totals, explicit Mythics, allowlisted profile caching, corruption/cancellation safety and shared Retry-After cooldown. Synthetic HTTP only.");
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
