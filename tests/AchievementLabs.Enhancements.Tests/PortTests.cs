using AchievementLabs.MultiSelect;
using System.Reflection;
using System.Net;

public static class PortTests
{
 public sealed class Session {public string EventsToken{get;set;}="";}
 public sealed class Model {public string EventTokenInput{get;set;}="";}
 static void Assert(bool value,string message){if(!value)throw new Exception(message);}
 sealed class Handler:HttpMessageHandler
 {
  public int Calls;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {
   Calls++;Assert(r.Method==HttpMethod.Get && r.RequestUri!.Host=="profile.xboxlive.com","Read-only profile destination");
   Assert(r.Headers.GetValues("Authorization").Single()=="synthetic-auth","Configured authorization");
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
  }
 }
 public static void Run()
 {
  QueuePresenceTokenTests.Run();
  var h=new Handler();using var http=new HttpClient(h);
  Assert(PresenceDiagnostics.Check(http,"synthetic-auth","1",CancellationToken.None).GetAwaiter().GetResult()==200 && h.Calls==1,"Read-only auth test");
  var response=PresenceDiagnostics.Observe(Task.FromResult((401,"private-response-must-not-be-displayed")),new object(),"638965135").GetAwaiter().GetResult();
  Assert(response.Item1==401 && PresenceDiagnostics.Explain(401).Contains("event token is separate"),"401 preserved and explained");
  Assert(PresenceDiagnostics.Explain(200).Contains("does not guarantee"),"No false playtime promise");
  Console.WriteLine("PASS: token display, manual-edit preservation, explicit reveal source, clear behavior and read-only presence diagnostics. No real credentials used.");
 }
}
