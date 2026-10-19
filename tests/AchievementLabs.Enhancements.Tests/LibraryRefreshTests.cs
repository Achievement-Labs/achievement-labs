using System.Net;
using System.Text;
using AchievementLabs.Core;

public static class LibraryRefreshTests
{
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    sealed class Sink:IProgress<LibraryRefreshProgress>{ public List<LibraryRefreshProgress> Values=[];public void Report(LibraryRefreshProgress p)=>Values.Add(p); }
    sealed class UnknownLength(byte[] bytes):HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length){length=0;return false;}
    }
    sealed class Fixture:HttpMessageHandler
    {
        public bool Unknown;public int Calls;public HttpStatusCode Status=HttpStatusCode.OK;
        public byte[] Bytes=Encoding.UTF8.GetBytes("{\"titles\":[{\"titleId\":\"1\",\"name\":\"Fixture\"}]}"+new string(' ',140000));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Assert(request.Method==HttpMethod.Get && request.RequestUri!.Host=="titlehub.xboxlive.com","Only library-read transport exercised");
            return Task.FromResult(new HttpResponseMessage(Status){Content=Unknown?new UnknownLength(Bytes):new ByteArrayContent(Bytes)});
        }
    }
    public static void Run()
    {
        foreach(var unknown in new[]{false,true}){
            var fixture=new Fixture{Unknown=unknown};using var client=new XboxApiClient("fixture",fixture);var sink=new Sink();
            var result=client.GetGamesListAsync("0",sink).GetAwaiter().GetResult();
            Assert(result!.Titles.Count==1 && fixture.Calls==1,"Refresh uses one bulk library request");
            Assert(sink.Values.Count>2 && sink.Values.Last().BytesRead==fixture.Bytes.Length && sink.Values.Last().Titles==1,"Actual streamed bytes and parsed titles reported");
            Assert(sink.Values.Select(p=>p.BytesRead).SequenceEqual(sink.Values.Select(p=>p.BytesRead).OrderBy(n=>n)),"Byte progress is monotonic");
            Assert(unknown?sink.Values.All(p=>p.TotalBytes==null):sink.Values.All(p=>p.TotalBytes==fixture.Bytes.Length),"Known and unknown content lengths remain distinct");
        }
        var failed=new Fixture{Status=HttpStatusCode.ServiceUnavailable};using var failedClient=new XboxApiClient("fixture",failed);bool rejected=false;
        try{failedClient.GetGamesListAsync("0").GetAwaiter().GetResult();}catch(HttpRequestException){rejected=true;}
        Assert(rejected,"HTTP refresh failure is surfaced before parsing");
        using var cancel=new CancellationTokenSource();cancel.Cancel();var cancelled=new Fixture();using var cancelledClient=new XboxApiClient("fixture",cancelled);rejected=false;
        try{cancelledClient.GetGamesListAsync("0",null,cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){rejected=true;}
        Assert(rejected,"Cancellation is supported by library download");
        Console.WriteLine("PASS: actual bulk library request, streamed known/unknown byte progress, title counts, HTTP failure and cancellation. All HTTP intercepted locally.");
    }
}
