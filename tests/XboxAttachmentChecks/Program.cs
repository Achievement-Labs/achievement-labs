using AchievementLabs.Core;
using System.Reflection;
using System.Text;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static object? Invoke(string method, object value) => typeof(XboxPcAppAuthorizationReader)
    .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [value]);
static string? Extract(string value) => (string?)Invoke("ExtractAuthorization", value);
static IReadOnlyList<string> Select(params string[] values) => (IReadOnlyList<string>)Invoke("SelectCandidates", values)!;
static string Token(int minutes, string hash = "999") => "XBL3.0 x=" + hash + ";e30." +
    Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":" + DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds() + "}"))
    .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";

var fresh = Token(60);
foreach (var suffix in new[] { "", "\0garbage", "\r\nheader", "\"json", ";other", " trailing", "\u0080data" })
    Check(Extract(fresh + suffix) == fresh, "Authorization boundary was not preserved.");
foreach (var malformed in new[] { "", "XBL3.0 x=", "XBL3.0 x=;token", "XBL3.0 x=999;", "XBL3.0 x=9 99;token", "prefix " + fresh })
    Check(Extract(malformed) == null, "Malformed authorization accepted.");
Check(Select(fresh).SequenceEqual([fresh]), "Valid single-copy session rejected.");
Check(Select(fresh, fresh + "\0data").Count == 1, "Equivalent memory strings were not deduplicated.");
Check(Select(Token(-10), "garbage", fresh).SequenceEqual([fresh]), "Expired or malformed candidate retained.");
var older = Token(10);
Check(Select(older, fresh)[0] == fresh, "Freshness order changed.");
Check(Select(older, older, older, older, fresh)[0] == older, "Repeated-copy preference changed.");
Check(Select(Enumerable.Range(0, 12).Select(i => Token(60, (1000 + i).ToString())).ToArray()).Count == 8, "Candidate limit changed.");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { await XboxPcAppAuthorizationReader.TryReadAsync(cancelled.Token); throw new Exception("Cancellation ignored."); }
catch (OperationCanceledException) { }
try { await XboxPcAppAuthorizationReader.TryReadValidatedSessionAsync(null, false, cancelled.Token); throw new Exception("Cancellation ignored."); }
catch (OperationCanceledException) { }
// Open only this test process; do not scan the Xbox app or use a live account.
var memory = new Memory.Mem();
try
{
    Check(!memory.OpenProcess(0, out _, readOnly: true), "Invalid PID accepted.");
    Check(memory.OpenProcess(Environment.ProcessId, out _, readOnly: true), "Read-only self-process access failed.");
    using var handle = new Microsoft.Win32.SafeHandles.SafeProcessHandle(memory.mProc.Handle, ownsHandle: true);
    Check(!handle.IsInvalid && memory.mProc.MainModule == null, "Read-only access queried modules.");
}
finally { memory.mProc.Process?.Dispose(); }
Console.WriteLine("Xbox attachment offline checks passed.");
