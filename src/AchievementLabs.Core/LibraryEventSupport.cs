namespace AchievementLabs.Core;

// CatalogRevision/CatalogSource/CatalogError/CustomOverride describe which catalog tier answered for the title
// (see CatalogPrecedence). CatalogSource is "bundled", "catalog" or "custom"; CustomOverride marks a custom pack
// shadowing an official title.
public sealed record LibraryEventSupport(bool Available, bool FullySupported, int Mapped, string? Note = null, bool PartiallySupported = false,
    int? CatalogRevision = null, string? CatalogSource = null, string? CatalogError = null, bool CustomOverride = false);
