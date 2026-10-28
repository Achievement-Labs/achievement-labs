using AchievementLabs.Core;
int reads=0;
var result=await AchievementUnlockVerifier.VerifyAsync(_=>Task.FromResult<AchievementProgressSnapshot?>(new(++reads==3?"Achieved":"InProgress","69/25")),default,(_,_)=>Task.CompletedTask);
if(!result.Confirmed||reads!=3)throw new Exception("Delayed confirmation failed");
reads=0;result=await AchievementUnlockVerifier.VerifyAsync(_=>{reads++;return Task.FromResult<AchievementProgressSnapshot?>(new("InProgress","69/25"));},default,(_,_)=>Task.CompletedTask);
if(result.Confirmed||reads!=5)throw new Exception("Counter value was treated as a confirmed unlock");
result=await AchievementUnlockVerifier.VerifyAsync(_=>Task.FromResult<AchievementProgressSnapshot?>(null),default,(_,_)=>Task.CompletedTask);if(result.Confirmed)throw new Exception("Missing row confirmed");
using var stop=new CancellationTokenSource();stop.Cancel();try{await AchievementUnlockVerifier.VerifyAsync(_=>throw new Exception("Read after cancel"),stop.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
Console.WriteLine("PASS: native desktop verification polls read-only, waits for Achieved, rejects counter-only/missing confirmation, and honours cancellation. No Xbox requests.");
