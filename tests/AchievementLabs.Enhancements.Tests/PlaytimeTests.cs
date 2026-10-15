using AchievementLabs.MultiSelect;
using System.Net;
using System.Net.Http;
using System.Text.Json;

static class PlaytimeTests
{
    static void Check(bool b,string message){if(!b)throw new Exception(message);}
    static string Response(string minutes,string title="123",string account="456",string name="MinutesPlayed")=>JsonSerializer.Serialize(new {
        statlistscollection=new[]{new {arrangebyfield="xuid",arrangebyfieldid=account,stats=new[]{new{titleid=title,name,value=minutes}}}}
    });
    sealed class Fake:HttpMessageHandler
    {
        public string Json=Response("1501");public HttpStatusCode Status=HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken token)
        {
            Check(r.RequestUri!.ToString()=="https://userstats.xboxlive.com/batch","Only statistics-read endpoint used");
            Check(r.Method==HttpMethod.Post,"Batch read uses POST");
            Check(r.Headers.GetValues("x-xbl-contract-version").Single()=="2","Correct contract version");
            using var body=JsonDocument.Parse(await r.Content!.ReadAsStringAsync(token));var root=body.RootElement;
            Check(root.GetProperty("xuids")[0].GetString()=="456","Account scoped");
            Check(root.GetProperty("stats")[0].GetProperty("name").GetString()=="MinutesPlayed","Correct statistic");
            Check(root.GetProperty("stats")[0].GetProperty("titleid").GetString()=="123","Title scoped");
            return new HttpResponseMessage(Status){Content=new StringContent(Json)};
        }
    }
    public static void Run()
    {
        Check(XboxPlaytime.Parse(Response("1501"),"456","123")==1501m,"Minutes parsed");
        Check(XboxPlaytime.Format(1501m)=="25 h 01 min","Total hours never wrap at 24");
        Check(XboxPlaytime.Format(59.9m)=="0 h 59 min","Fractional minutes rounded down");
        Check(XboxPlaytime.Parse(Response("0"),"456","123")==0m,"Real zero retained");
        Check(XboxPlaytime.Parse("{}","456","123")==null,"Missing is not zero");
        Check(XboxPlaytime.Parse(Response("5","999"),"456","123")==null,"Wrong title ignored");
        Check(XboxPlaytime.Parse(Response("5","123","999"),"456","123")==null,"Wrong account ignored");
        Check(XboxPlaytime.Parse(Response("5",name:"GamesPlayed"),"456","123")==null,"Other stats ignored");
        Check(XboxPlaytime.Parse(Response("-1"),"456","123")==null,"Negative invalid");
        Check(XboxPlaytime.Parse(Response("NaN"),"456","123")==null,"Non-numeric invalid");
        string nested="{\"groups\":["+Response("90")+"]}";
        Check(XboxPlaytime.Parse(nested,"456","123")==90m,"Grouped response parsed");
        Check(XboxPlaytime.Parse(Response("90").Replace("titleid","TitleId"),"456","123")==90m,"Property casing tolerated");
        using var fake=new Fake();using var http=new HttpClient(fake);
        Check(XboxPlaytime.Read(http,"456","123",CancellationToken.None).GetAwaiter().GetResult()==1501m,"Read and request verified");
        fake.Status=HttpStatusCode.TooManyRequests;
        try{XboxPlaytime.Read(http,"456","123",CancellationToken.None).GetAwaiter().GetResult();throw new Exception("429 ignored");}catch(XboxPlaytime.RateLimited){}
        fake.Status=HttpStatusCode.Unauthorized;
        try{XboxPlaytime.Read(http,"456","123",CancellationToken.None).GetAwaiter().GetResult();throw new Exception("401 ignored");}catch(UnauthorizedAccessException){}
        Console.WriteLine("PASS: Xbox playtime parsing, account/title isolation, missing vs zero, total hours, grouped responses, request body and rate-limit/authentication handling. All HTTP intercepted locally.");
    }
}
