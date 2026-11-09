using System.Net;
using System.Text;
using System.Text.Json;
using AchievementLabs.Core;

static class PublicCatalogChecks
{
    sealed class Handler(Func<HttpRequestMessage,string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Method!=HttpMethod.Get || request.RequestUri!.Host!="achievementlabs.org" || request.Headers.Authorization!=null || request.Headers.Contains("Cookie")) throw new Exception("Private transport data leaked");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(reply(request),Encoding.UTF8,"application/json")});
        }
    }
    public static async Task LiveAsync()
    {
        using var http=PublicCatalogClient.CreateHttpClient();var client=new PublicCatalogClient(http);
        var page=await client.SearchAsync("219630713",default);
        if(page.Items.Length!=1 || page.Items[0].Status!="Supported")throw new Exception("Hosted Halo status mismatch");
        var totals=await client.TotalsAsync(new[]{"1144039928","219630713"},default);
        if(totals.Length!=2 || totals.Any(t=>t.Total<=0))throw new Exception("Hosted totals unavailable");
        Console.WriteLine($"PASS: production C# client reads hosted search and totals over HTTPS; revision {page.CatalogRevision}.");
    }
    public static async Task RunAsync()
    {
        var calls=0; var badRevision=false; var badId=false;
        using var http=new HttpClient(new Handler(r=>{
            calls++;
            var ids=Uri.UnescapeDataString(r.RequestUri!.Query.Split("ids=")[1]).Split(',');
            return JsonSerializer.Serialize(new {catalogRevision=new string(badRevision && calls==2?'b':'a',64),sourceRevision=new string('c',40),
                items=ids.Select(id=>new {titleId=badId?"4294967294":id,total=100,platforms=new[]{"PC"},checkedAt="2026-01-01T00:00:00Z"}),missingIds=Array.Empty<string>()});
        }));
        var client=new PublicCatalogClient(http);var ids=Enumerable.Range(1,101).Select(i=>i.ToString()).ToArray();
        var totals=await client.TotalsAsync(ids,default);
        if(calls!=2 || totals.Length!=101)throw new Exception("Totals must batch at 100 IDs");
        calls=0;badRevision=true;
        try{await client.TotalsAsync(ids,default);throw new Exception("Mixed revisions accepted");}catch(InvalidDataException){}
        calls=0;badRevision=false;badId=true;
        try{await client.TotalsAsync(ids,default);throw new Exception("Foreign ID accepted");}catch(InvalidDataException){}
        var halo=PublicCatalogClient.BundledSearch("219630713");
        if(halo.TotalMatched!=1 || halo.Items[0].Status!="Supported")throw new Exception("Bundled Halo status incorrect");
        using var failing=new HttpClient(new Handler(_=>throw new HttpRequestException("Offline")));
        try{await new PublicCatalogClient(failing).SearchAsync("halo",default);throw new Exception("Offline response accepted");}catch(HttpRequestException){}
        var offline=PublicCatalogClient.BundledSearch("halo");if(!offline.Offline || offline.TotalMatched==0)throw new Exception("Offline search unavailable");
        var shared=SharedAchievementTotals.Bundled();var before=shared.Count;
        try{shared.Merge("{\"schemaVersion\":1,\"entries\":[{\"titleId\":\"1\",\"total\":-1}]}");throw new Exception("Bad cache accepted");}catch(InvalidDataException){}
        if(shared.Count!=before)throw new Exception("Bad update changed cache");
        Console.WriteLine("PASS: public-only HTTP, 100-ID batching, revision consistency, foreign-ID rejection, offline search/status and invalid-cache preservation.");
    }
}
