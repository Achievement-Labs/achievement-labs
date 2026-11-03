using AchievementLabs.Models;

namespace AchievementLabs.Services.Xbox360;

public interface IXbox360PackageService
{
    Xbox360PackageInfo InspectPackage(string filePath);
    byte[] ReadEntryBytes(string filePath, string entryPath);
}
