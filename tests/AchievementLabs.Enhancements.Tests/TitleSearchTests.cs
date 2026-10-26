using AchievementLabs.Core;

static class TitleSearchTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        Check(TitleSearchText.Variants("Black Ops 3").Contains("Black Ops III"), "Arabic query finds Roman title");
        Check(TitleSearchText.Variants("Black Ops III").Contains("Black Ops 3"), "Roman query finds Arabic title");
        Check(TitleSearchText.Variants("Final Fantasy 13-2").Contains("Final Fantasy XIII-2"), "Mixed numeral spellings retained");
        Check(TitleSearchText.Matches("Call of Duty: Black Ops III", "black ops 3"), "Library uses numeral aliases");
        Check(TitleSearchText.Matches("Civilization 6", "Civilization VI"), "Reverse library alias");
        Check(!TitleSearchText.Matches("Black Ops II", "Black Ops 3"), "Different installment remains distinct");
        Check(TitleSearchText.Variants("I Am Alive").SequenceEqual(new[]{"I Am Alive"}), "Pronoun I remains unchanged");
        Check(TitleSearchText.Variants("Civilization MIXED 007").Length == 1, "Word fragments and leading zeros unchanged");
        Check(TitleSearchText.Variants("Game 1 2 3 4 5 6 7 8").Length <= 8, "Variant requests bounded");
        Check(TitleSearchPlatforms.Classify(["Xbox One", "PC"]).SetEquals(["one", "pc"]), "Multi-platform titles match either selected platform");
        Check(TitleSearchPlatforms.Classify(["PC", "Xbox360"]).SetEquals(["gfwl"]), "GFWL is not mislabeled Xbox 360");
        Check(TitleSearchPlatforms.Classify(["Windows 8"]).SetEquals(["win8"]), "Legacy Windows separated");
        Check(TitleSearchPlatforms.Classify([]).SetEquals(["other"]), "Unknown platform remains discoverable");
        Check(TitleSearchPlatforms.Classify(["SCARLETT"]).SetEquals(["series"]), "Series alias recognized");
        Console.WriteLine("PASS: numeral aliases, mixed spellings, bounded requests and platform edition filtering. No network.");
    }
}
