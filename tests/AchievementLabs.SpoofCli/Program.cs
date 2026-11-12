// Headless spoofing test CLI: exercise the presence/spoofing path in isolation,
// without the GUI. Mirrors the Xbox-Achievement-Unlocker XauSpoofCli workflow.
//
//   dotnet run --project tests/AchievementLabs.SpoofCli -c Release -- scan
//   dotnet run --project tests/AchievementLabs.SpoofCli -c Release -- probe [--title ID]
//   dotnet run --project tests/AchievementLabs.SpoofCli -c Release -- run --title ID [--minutes N]
//   dotnet run --project tests/AchievementLabs.SpoofCli -c Release -- stop
//
// probe/run read the signed-in Xbox app token from memory (validated against the
// profile API) and print HTTP status AND response body per presence URI. Tokens are
// never printed. A `run` is a REAL spoof: presence is left set until `stop` clears it.
using System.Diagnostics;
using AchievementLabs.Core;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault() ?? "help";
        string? title = null, xuid = null; int minutes = 10;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "--title" or "--xuid" && i + 1 < args.Length)
            { if (args[i] == "--title") title = args[++i]; else xuid = args[++i]; }
            else if (args[i] == "--minutes" && i + 1 < args.Length) minutes = int.Parse(args[++i]);
        }
        try
        {
            switch (command)
            {
                case "scan":
                {
                    var ids = XboxPcAppAuthorizationReader.FindProcessIds();
                    Console.WriteLine(ids.Length == 0
                        ? "No Xbox app process found (XboxPcApp / XboxPcAppCE / XboxPcAppFT)."
                        : $"Xbox app processes: {string.Join(", ", ids)}");
                    return ids.Length == 0 ? 1 : 0;
                }
                case "probe":
                    return await ProbeAsync(xuid, title);
                case "run":
                    if (string.IsNullOrWhiteSpace(title)) { Console.Error.WriteLine("run requires --title ID"); return 2; }
                    return await RunAsync(xuid, title, minutes);
                case "stop":
                    return await StopAsync(xuid);
                default:
                    Console.WriteLine("usage: SpoofCli scan | probe [--title ID] | run --title ID [--minutes N] | stop [--xuid XUID]");
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    static async Task<(XboxPcAppAuthorizationReader.ValidatedSession Session, XboxApiClient Client)> SessionAsync(string? xuid)
    {
        Console.WriteLine("Scanning the Xbox app for a signed-in token…");
        var session = await XboxPcAppAuthorizationReader.TryReadValidatedSessionAsync(xuid, regionOverride: false,
            report: message => Console.WriteLine($"  {message}"));
        if (session == null) throw new InvalidOperationException("No validated Xbox session found. Sign in to the Xbox app first.");
        Console.WriteLine($"Validated session: xuid {session.Profile.ProfileUsers.FirstOrDefault()?.Id}, pid {session.ProcessId}.");
        return (session, new XboxApiClient(session.Authorization));
    }

    static async Task<int> ProbeAsync(string? xuid, string? title)
    {
        var (session, client) = await SessionAsync(xuid);
        using (client)
        {
            var profile = session.Profile.ProfileUsers.FirstOrDefault();
            Console.WriteLine($"Profile settings: {string.Join(", ", profile?.Settings.Select(s => $"{s.Id}={s.Value}") ?? Array.Empty<string>())}");
            if (title == null) return 0;
            var me = profile?.Id ?? "";
            var body = SpoofRequest.BuildHeartbeatBody(title);
            Console.WriteLine($"Heartbeat body: {body}");
            var result = await client.SendHeartbeatAsync(me, title);
            Console.WriteLine($"SendHeartbeatAsync: HTTP {result.StatusCode}");
            Console.WriteLine(result.Body);
            return result.StatusCode is >= 200 and < 300 ? 0 : 1;
        }
    }

    static async Task<int> RunAsync(string? xuid, string title, int minutes)
    {
        var (session, client) = await SessionAsync(xuid);
        using (client)
        {
            var me = session.Profile.ProfileUsers.FirstOrDefault()?.Id ?? "";
            var until = DateTimeOffset.UtcNow.AddMinutes(minutes);
            Console.WriteLine($"Spoofing title {title} for {minutes} minute(s). Stop with: SpoofCli stop --xuid {me}");
            int attempt = 0, accepted = 0;
            while (DateTimeOffset.UtcNow < until)
            {
                var (status, body) = await client.SendHeartbeatAsync(me, title);
                attempt++; accepted += status is >= 200 and < 300 ? 1 : 0;
                Console.WriteLine($"heartbeat #{attempt}: HTTP {status} {body}");
                await Task.Delay(TimeSpan.FromMinutes(5));
            }
            Console.WriteLine($"Heartbeat window elapsed: {accepted}/{attempt} accepted.");
            var stop = await client.StopHeartbeatAsync(me);
            Console.WriteLine($"Stop cleanup: HTTP {stop.StatusCode}");
            return accepted > 0 ? 0 : 1;
        }
    }

    static async Task<int> StopAsync(string? xuid)
    {
        var (session, client) = await SessionAsync(xuid);
        using (client)
        {
            var me = session.Profile.ProfileUsers.FirstOrDefault()?.Id ?? "";
            var stop = await client.StopHeartbeatAsync(me);
            Console.WriteLine($"Stop cleanup: HTTP {stop.StatusCode}");
            Console.WriteLine(stop.Body);
            return stop.StatusCode is >= 200 and < 300 ? 0 : 1;
        }
    }
}
