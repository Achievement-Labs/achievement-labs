namespace AchievementLabs.Core;
public sealed record LibraryRefreshProgress(long BytesRead, long? TotalBytes, int Titles = 0);
