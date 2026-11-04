namespace AchievementLabs.Core;

public sealed record LibraryEventSupport(bool Available, bool FullySupported, int Mapped, string? Note = null);
