using System.Globalization;
using SAM.API;

// Usage:
//   AchievementLabs.SteamIdle <app-id>                        Idle as the game until killed (prints "READY <app-id>").
//   AchievementLabs.SteamIdle <app-id> --set <id> --clear <id>  Change achievements, then exit (prints "STORED <count>").
// Running as its own process gives each game a fresh Steam connection, as Steam Achievement Manager does;
// a process stays tied to the first game it connects as.
if (args.Length == 0 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
{
    Console.Error.WriteLine("Usage: AchievementLabs.SteamIdle <steam-app-id> [--set <achievement>]... [--clear <achievement>]...");
    return 64;
}

var changes = new List<(string Id, bool Unlock)>();
for (var i = 1; i < args.Length; i++)
{
    if (i + 1 >= args.Length || args[i] is not ("--set" or "--clear"))
    {
        Console.Error.WriteLine($"Unexpected argument: {args[i]}");
        return 64;
    }
    changes.Add((args[i + 1], args[i] == "--set"));
    i++;
}

Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(CultureInfo.InvariantCulture), EnvironmentVariableTarget.Process);
Environment.SetEnvironmentVariable("SteamGameId", appId.ToString(CultureInfo.InvariantCulture), EnvironmentVariableTarget.Process);

using var client = new Client();
try
{
    client.Initialize(appId);
    if (client.SteamUser is null || !client.SteamUser.IsLoggedIn())
    {
        Console.Error.WriteLine("Steam is not logged in.");
        return 2;
    }

    if (changes.Count > 0) return StoreChanges(client, (ulong)appId, changes);

    Console.Out.WriteLine($"READY {appId}");
    Console.Out.Flush();

    while (true)
    {
        client.RunCallbacks(false);
        Thread.Sleep(1000);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int StoreChanges(Client client, ulong appId, List<(string Id, bool Unlock)> changes)
{
    if (client.SteamUserStats is null)
    {
        Console.Error.WriteLine("Steam's user-stats interface isn't available for this game.");
        return 3;
    }

    // Steam rejects SetAchievement until it confirms this game's stats are loaded for this user.
    int? result = null;
    var received = client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsReceived>();
    received.OnRun += data => { if ((data.GameId & 0xFFFFFF) == appId) result = data.Result; };
    if (client.SteamUserStats.RequestUserStats(client.SteamUser.GetSteamId()) == CallHandle.Invalid)
    {
        Console.Error.WriteLine("Steam refused to load this game's stats.");
        return 3;
    }
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (result == null && DateTime.UtcNow < deadline)
    {
        client.RunCallbacks(false);
        Thread.Sleep(50);
    }
    if (result == null)
    {
        Console.Error.WriteLine("Steam didn't load this game's stats within 15 seconds. Make sure Steam is online.");
        return 4;
    }
    if (result != 1)
    {
        Console.Error.WriteLine($"Steam couldn't load this game's stats (result {result}). The account may not own this game.");
        return 4;
    }

    foreach (var (id, unlock) in changes)
    {
        if (!client.SteamUserStats.SetAchievement(id, unlock))
        {
            Console.Error.WriteLine($"Steam refused to {(unlock ? "set" : "clear")} achievement '{id}'. It may not exist in this game.");
            return 5;
        }
    }
    if (!client.SteamUserStats.StoreStats())
    {
        Console.Error.WriteLine("Steam refused to save the changes.");
        return 6;
    }

    // Give the Steam client a moment to accept the save before this process disconnects.
    var settle = DateTime.UtcNow.AddSeconds(2);
    while (DateTime.UtcNow < settle)
    {
        client.RunCallbacks(false);
        Thread.Sleep(50);
    }
    Console.Out.WriteLine($"STORED {changes.Count}");
    Console.Out.Flush();
    return 0;
}
