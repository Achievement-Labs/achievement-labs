using System.Globalization;
using System.Text.RegularExpressions;

namespace AchievementLabs.Core;

/// <summary>Search aliases only; never changes a catalogue name or title identity.</summary>
public static class TitleSearchText
{
    private static readonly string[] Romans = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX", "XXI", "XXII", "XXIII", "XXIV", "XXV", "XXVI", "XXVII", "XXVIII", "XXIX", "XXX"];
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant);
    private static string? Alternate(Match word)
    {
        if (int.TryParse(word.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number is >= 1 and <= 30 && word.Value == number.ToString(CultureInfo.InvariantCulture)) return Romans[number];
        number = Array.IndexOf(Romans, word.Value.ToUpperInvariant());
        return number > 0 && !(number == 1 && word.Index == 0) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }
    public static string NormalizeLookalikes(string value)
    {
        if(!value.Any(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))return value;
        const string lookalikes="\u0410\u0430\u0412\u0415\u0435\u041a\u043a\u041c\u041d\u041e\u043e\u0420\u0440\u0421\u0441\u0422\u0442\u0443\u0423\u0425\u0445";
        const string latin="AaBEeKkMHOoPpCcTtyYXx";
        return new string(value.Select(c=>{var index=lookalikes.IndexOf(c);return index<0?c:latin[index];}).ToArray());
    }
    public static string Key(string value) => string.Join(" ", Words.Matches(NormalizeLookalikes(value)).Select(word =>
    {
        var number = Array.IndexOf(Romans, word.Value.ToUpperInvariant());
        return number > 0 && !(number == 1 && word.Index == 0) ? number.ToString(CultureInfo.InvariantCulture) : word.Value.ToLowerInvariant();
    }));
    public static bool Matches(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var key = Key(query);
        return key.Length > 0 ? Key(name).Contains(key, StringComparison.Ordinal) : name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    public static string[] Variants(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 200) throw new ArgumentException("Enter a game name up to 200 characters.");
        var result = new List<string> { input };
        void Add(string value) { if (value.Length <= 200 && !result.Contains(value, StringComparer.OrdinalIgnoreCase) && result.Count < 8) result.Add(value); }
        var words = Words.Matches(input).Where(word => Alternate(word) != null).Take(6).ToArray();
        foreach (var word in words) Add(input[..word.Index] + Alternate(word) + input[(word.Index + word.Length)..]);
        Add(Words.Replace(input, word => Alternate(word) ?? word.Value));
        return result.ToArray();
    }
}

public static class TitleSearchPlatforms
{
    public static readonly (string Key,string Label)[] Choices=[("series","Xbox Series X|S"),("one","Xbox One"),("360","Xbox 360"),("pc","PC"),("gfwl","GFWL"),("win32","Win32"),("win8","Windows 8"),("mobile","Mobile"),("ios","iOS"),("android","Android"),("phone","Windows Phone"),("web","Web"),("appletv","Apple TV"),("kindle","Kindle Fire"),("vr","Gear VR"),("switch","Nintendo Switch"),("other","Other / unknown")];
    public static HashSet<string> Classify(IEnumerable<string> values)
    {
        var names=values.Select(x=>Regex.Replace(x.ToUpperInvariant(),@"[^A-Z0-9]","")).ToArray();
        if(names.Contains("GFWL"))return names.Contains("XBOX360") ? ["gfwl","360"] : ["gfwl"];
        if(names.Contains("PC") && names.Contains("XBOX360"))return ["gfwl"];
        return names.SelectMany(name=>name switch
        {
            "XBOXSERIES" or "XBOXSERIESXS" or "SCARLETT"=>new[]{"series"},"XBOXONE"=>["one"],"XBOX360"=>["360"],
            "PC" or "WINDOWS" or "WINDOWS10"=>["pc"],"WIN32"=>["win32"],"WIN8" or "WINDOWS8" or "WINDOWS81"=>["win8"],
            "MOBILE"=>["mobile"],"WINDOWSPHONE"=>["phone","mobile"],"IOS"=>["ios","mobile"],"ANDROID"=>["android","mobile"],
            "WEB"=>["web"],"APPLETV"=>["appletv"],"KINDLEFIRE"=>["kindle","mobile"],"GEARVR"=>["vr"],"NINTENDOSWITCH" or "SWITCH"=>["switch"],_=>["other"]
        }).DefaultIfEmpty("other").ToHashSet(StringComparer.Ordinal);
    }
}
