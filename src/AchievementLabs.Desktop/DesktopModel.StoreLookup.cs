using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private string storeProductInput = "", storeLookupStatus = "Paste a Microsoft/Xbox Store link or a 12-character product ID.", storeTitleId = "", storeTitleName = "";
    public string StoreProductInput { get => storeProductInput; set { storeProductInput = value; Changed(); StoreTitleId = ""; StoreTitleName = ""; } }
    public string StoreLookupStatus { get => storeLookupStatus; private set { storeLookupStatus = value; Changed(); } }
    public string StoreTitleId { get => storeTitleId; private set { storeTitleId = value; Changed(); Changed(nameof(HasStoreTitleId)); } }
    public string StoreTitleName { get => storeTitleName; private set { storeTitleName = value; Changed(); } }
    public bool HasStoreTitleId => StoreTitleId.Length > 0;
    public static string? ParseStoreProductId(string input)
    {
        var match = Regex.Match(input.Trim(), @"(?<![A-Za-z0-9])[A-Za-z0-9]{12}(?![A-Za-z0-9])", RegexOptions.CultureInvariant);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }
    public async Task ResolveStoreTitleAsync()
    {
        if (busy) return;
        StoreTitleId = ""; StoreTitleName = "";
        var productId = ParseStoreProductId(StoreProductInput);
        if (productId == null) { StoreLookupStatus = "Enter a 12-character Store product ID or a link containing one."; return; }
        Busy(true);
        var input = StoreProductInput;
        try
        {
            StoreLookupStatus = "Looking up Microsoft's Store catalogue…";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var market = Uri.TryCreate(input, UriKind.Absolute, out var uri) ? Regex.Match(uri.AbsolutePath, @"/([a-zA-Z]{2})-([a-zA-Z]{2})(?:/|$)").Groups[2].Value : "";
            if (market.Length == 0) market = "US";
            var json = JObject.Parse(await http.GetStringAsync($"https://displaycatalog.mp.microsoft.com/v7.0/products/{productId}?market={market}&languages=en-us", lifetime.Token));
            if (input != StoreProductInput) return;
            var product = json["Product"];
            var ids = (product?["AlternateIds"] as JArray ?? []).Where(x => string.Equals(x["IdType"]?.ToString(), "XboxTitleId", StringComparison.OrdinalIgnoreCase))
                .Select(x => x["Value"]?.ToString()).Where(x => uint.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0).Distinct().ToArray();
            StoreTitleName = product?["LocalizedProperties"]?.FirstOrDefault()?["ProductTitle"]?.ToString() ?? productId;
            if (ids.Length != 1) { StoreLookupStatus = ids.Length == 0 ? "This Store product does not publish an Xbox Title ID. Bundles and add-ons may have no title mapping." : "This product has multiple Title IDs; choose the individual game's Store listing."; return; }
            StoreTitleId = ids[0]!;
            StoreLookupStatus = "Xbox Title ID found. Copy it or open achievements. This mapping does not confirm achievement or event support.";
        }
        catch (OperationCanceledException) { StoreLookupStatus = "Lookup cancelled or timed out. Try again."; }
        catch { StoreLookupStatus = "Store lookup unavailable. Check the product ID, region and connection, then retry."; }
        finally { Busy(false); }
    }
}
