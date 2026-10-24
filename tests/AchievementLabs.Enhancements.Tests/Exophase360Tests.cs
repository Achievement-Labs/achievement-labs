using System.Net;
using AchievementLabs.Services.Xbox360;

static class Exophase360Tests
{
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Count++;
            if(request.Headers.Authorization != null || request.Content != null) throw new Exception("Unexpected private request data");
            return Task.FromResult(respond(request));
        }
    }
    static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) {Content=new StringContent(value)};
    public static Exophase360Client Fixture(string gameName,string achievementName,bool blocked=false) => new(new Handler(r => {
        if (blocked) return new(HttpStatusCode.Forbidden);
        if (r.RequestUri!.Host=="www.exophase.com") return Text("<script>window.playerProfileId = '123';</script>");
        if (r.RequestUri.AbsolutePath.EndsWith("/earned")) return Text(System.Text.Json.JsonSerializer.Serialize(new {list=new[]{new {name=achievementName,timestamp=1609459200}}}));
        if (r.RequestUri.Query.Contains("page=1&")) return Text(System.Text.Json.JsonSerializer.Serialize(new {success=true,games=new[]{new {master_id=1,meta=new {title=gameName,platforms=new[]{new {slug="xbox360"}}}}}}));
        return Text("{\"success\":true,\"games\":[]}");
    }));
    public static void Run()
    {
        var handler=new Handler(r => {
            var uri=r.RequestUri!;
            if(uri.Host=="www.exophase.com") return Text("<script>window.playerProfileId = '123';</script>");
            if(uri.AbsolutePath.EndsWith("/earned")) return Text("""{"list":[{"name":"First Steps","timestamp":1609459200},{"name":"Offline Award","timestamp":0}]}""");
            if(uri.Query.Contains("page=1&")) return Text("""{"success":true,"games":[{"master_id":1,"meta":{"title":"Game III","platforms":[{"slug":"xbox360"}]}},{"master_id":2,"meta":{"title":"Game III","platforms":[{"slug":"xbox-one"}]}},{"master_id":3,"meta":{"title":"PC Game","platforms":[{"name":"Games for Windows LIVE"}]}}]}""");
            return Text("""{"success":true,"games":[]}""");
        });
        using(var api=new Exophase360Client(handler))
        {
            var games=api.FindGamesAsync("fixture user",true).GetAwaiter().GetResult();
            if(games.Count!=1 || games[0].Id!=1) throw new Exception("Only Xbox 360 games should be returned");
            var dates=api.LoadDatesAsync("fixture",false,games[0].Id).GetAwaiter().GetResult();
            if(dates.Count!=2 || dates[0].Time!=DateTimeOffset.Parse("2021-01-01T00:00:00Z") || dates[1].Time!=null) throw new Exception("Earned dates changed");
            var count=handler.Count;
            try { api.FindGamesAsync("https://elsewhere.example",false).GetAwaiter().GetResult(); throw new Exception("URL accepted as a profile name"); } catch(InvalidDataException) { }
            if(handler.Count!=count) throw new Exception("Invalid input caused a network request");
        }
        using(var blocked=new Exophase360Client(new Handler(_=>new(HttpStatusCode.Forbidden))))
        {
            try { blocked.FindGamesAsync("fixture",false).GetAwaiter().GetResult(); throw new Exception("Blocked response accepted"); }
            catch(InvalidOperationException e) when(e.Message.Contains("blocked")) { }
        }
        using(var redirect=new Exophase360Client(new Handler(_=> {var r=new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location=new Uri("https://other.example/"); return r;})))
        {
            try { redirect.FindGamesAsync("fixture",false).GetAwaiter().GetResult(); throw new Exception("External redirect accepted"); } catch(InvalidDataException) { }
        }
        using(var oversized=new Exophase360Client(new Handler(_=>Text(new string('x',2*1024*1024+1)))))
        {
            try { oversized.FindGamesAsync("fixture",false).GetAwaiter().GetResult(); throw new Exception("Oversized response accepted"); } catch(InvalidDataException) { }
        }
        Console.WriteLine("PASS: Exophase profile/gamertag lookup, Xbox 360 platform filtering, pagination, online/offline dates, input bounds, blocked requests and external redirects; fixture HTTP only, no Xbox credentials.");
    }
}
