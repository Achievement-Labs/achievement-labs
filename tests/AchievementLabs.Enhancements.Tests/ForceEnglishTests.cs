using System.Globalization;
using System.Net;
using System.Reflection;
using AchievementLabs.Core;
using AchievementLabs.Desktop;
using AchievementLabs.Desktop.Workflows;

internal static class ForceEnglishTests
{
    sealed class Wire : HttpMessageHandler
    {
        internal readonly List<string> Languages = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Languages.Add(string.Join(",", request.Headers.AcceptLanguage.Select(x => x.Value)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"profileUsers\":[]}") });
        }
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; var ui = CultureInfo.CurrentUICulture;
        var root = Path.Combine(Path.GetTempPath(), "labs-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var wire = new Wire();
            using var api = new XboxApiClient("synthetic", wire);
            await api.GetBasicProfileAsync(); api.SetRegionOverride(true);
            await api.GetBasicProfileAsync(); api.SetRegionOverride(false);
            await api.GetBasicProfileAsync();
            Check(wire.Languages.SequenceEqual(new[] { "fr-FR", "en-GB", "fr-FR" }), "Force English must update the existing client and use display language when off.");

            var account = new NativeAccountContext { XAUTH = "synthetic", RegionOverride = true, EventsDirectory = root };
            var statWire = new Wire();
            var vm = new StatsEditorViewModel(new NativeNotices(_ => { }), account,
                auth => new XboxApiClient(auth, statWire), allowRemoteCatalog: false);
            var get = typeof(StatsEditorViewModel).GetMethod("GetRestAPI", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var first = (XboxApiClient)get.Invoke(vm, null)!; await first.GetBasicProfileAsync();
            account.RegionOverride = false;
            var second = (XboxApiClient)get.Invoke(vm, null)!; await second.GetBasicProfileAsync();
            Check(ReferenceEquals(first, second) && statWire.Languages.SequenceEqual(new[] { "en-GB", "fr-FR" }), "Cached stats client ignored language changes.");
            vm.ReleaseClient();

            using var model = new DesktopModel();
            model.RegionOverride = true;
            var context = (NativeAccountContext)typeof(DesktopModel).GetField("queueAccount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
            Check(context.RegionOverride && model.Windows8.ForceEnglish, "Settings did not reach queue and Windows 8 workflows.");
            model.RegionOverride = false;
            Check(!context.RegionOverride && !model.Windows8.ForceEnglish, "Turning Force English off did not update existing workflows.");

            var store = new DesktopPreferencesStore(Path.Combine(root, "settings.json"));
            await store.SaveAsync(new() { EventsDirectory = root, SessionPath = Path.Combine(root, "session.json"), RegionOverride = true });
            Check((await store.LoadAsync()).RegionOverride, "Existing RegionOverride preference must remain compatible.");
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Check(XboxResponseLanguage.Resolve(false) == "en-GB", "Invariant culture must not create an empty language header.");
            Console.WriteLine("PASS: Force English request headers, immediate cached-client updates, stats/queue/Windows8 propagation, display-language fallback and saved preference compatibility. Synthetic transport only.");
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui; Directory.Delete(root, true); }
    }
}
