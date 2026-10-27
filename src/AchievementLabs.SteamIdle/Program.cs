using System.Globalization;
using SAM.API;

if (args.Length == 0 || !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
{
    Console.Error.WriteLine("Usage: AchievementLabs.SteamIdle <steam-app-id>");
    return 64;
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
