#if LABS_UPDATE_TESTING
using System.Net;
using AchievementLabs.Core;
using AchievementLabs.Desktop;

static class TestTransportChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "labs-private-feed-" + Guid.NewGuid().ToString("N"));
        var oldFeed = Environment.GetEnvironmentVariable("LABS_UPDATE_TEST_DIRECTORY");
        var oldProfile = Environment.GetEnvironmentVariable("LABS_UPDATE_TEST_ROOT");
        Directory.CreateDirectory(Path.Combine(root, "baseline"));
        try
        {
            File.WriteAllText(Path.Combine(root, "active.txt"), "baseline");
            File.WriteAllText(Path.Combine(root, "baseline", "releases.json"), "[]");
            Environment.SetEnvironmentVariable("LABS_UPDATE_TEST_DIRECTORY", root);
            Environment.SetEnvironmentVariable("LABS_UPDATE_TEST_ROOT", Path.Combine(root, "profile"));
            using var http = UpdateTestTransport.CreateClient();
            if (await http.GetStringAsync("https://api.github.com/repos/Achievement-Labs/achievement-labs/releases?per_page=100") != "[]") throw new Exception("Fixture metadata failed.");
            if ((await http.GetAsync("https://example.com/releases")).StatusCode != HttpStatusCode.Forbidden) throw new Exception("Private build can access another feed.");
            if ((await http.PostAsync("https://example.com/", new StringContent(""))).StatusCode != HttpStatusCode.BadRequest) throw new Exception("Private feed accepted writes.");
            if (!AchievementLabsPaths.LocalFile("session.bin").StartsWith(Path.Combine(root, "profile")) || !AppUpdater.Root.StartsWith(Path.Combine(root, "profile"))) throw new Exception("Update profile is not isolated.");
            Environment.SetEnvironmentVariable("LABS_UPDATE_TEST_DIRECTORY", null);
            if ((await http.GetAsync("https://api.github.com/repos/Achievement-Labs/achievement-labs/releases?per_page=100")).StatusCode != HttpStatusCode.ServiceUnavailable) throw new Exception("Missing fixture configuration did not fail closed.");
            Console.WriteLine("PASS: private transport stays offline, rejects other feeds/writes, isolates profile/update paths, and fails closed without its launcher.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LABS_UPDATE_TEST_DIRECTORY", oldFeed);
            Environment.SetEnvironmentVariable("LABS_UPDATE_TEST_ROOT", oldProfile);
            Directory.Delete(root, true);
        }
    }
}
#endif
