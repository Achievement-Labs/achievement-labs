namespace AchievementLabs.Core;

public sealed record LibrarySupportBadge(string Kind, string Symbol, string Label, string Detail)
{
	public string Colour => Kind switch
	{
		"supported" => "#8FE3BC",
		"partial" => "#F4C66A",
		"testing" => "#F4C66A",
		"unsupported" => "#F18B8B",
		_ => "#A6B4BF",
	};

	public static LibrarySupportBadge Unknown => new LibrarySupportBadge("unknown", "?", "Not checked", "Open achievements to identify this game's unlock method.");

	public static LibrarySupportBadge Resolve(bool legacy, bool? eventBased, bool hasEventEntry, bool available, bool fullySupported, int mapped, int total, bool partiallySupported = false)
	{
		if (legacy)
		{
			return new LibrarySupportBadge("unknown", "?", "Legacy", "Legacy titles use their separate profile/editor or read-only tools; this is not an online unlock support claim.");
		}
		if (eventBased == false && !hasEventEntry)
		{
			return new LibrarySupportBadge("supported", "✓", "Supported", "Title-based achievements: supported by the standard unlocker.");
		}
		if (!hasEventEntry)
		{
			if (!eventBased.GetValueOrDefault())
			{
				return Unknown;
			}
			return new LibrarySupportBadge("unsupported", "●", "Unsupported", "Event-based game: no event mapping is available in this app.");
		}
		if (!available)
		{
			return new LibrarySupportBadge("unsupported", "●", "Unsupported", "Event mapping is disabled, unavailable or has no mapped achievements.");
		}
		string text = $"Event catalogue: {mapped} mapped achievement IDs" + ((total > 0) ? $" / {total} known achievements" : "") + ". Catalogue support does not confirm an individual unlock.";
		if (!fullySupported)
		{
			return partiallySupported
				? new LibrarySupportBadge("partial", "⚠", "Partially Supported", text)
				: new LibrarySupportBadge("testing", "⚠", "Testing", text);
		}
		return new LibrarySupportBadge("supported", "✓", "Supported", text);
	}
}
