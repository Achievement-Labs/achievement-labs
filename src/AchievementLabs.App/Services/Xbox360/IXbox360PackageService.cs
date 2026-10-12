using AchievementLabs.Models;

namespace AchievementLabs.Services.Xbox360;

public interface IXbox360PackageService
{
    string HorizonInstallPath { get; }
    bool IsHorizonInstalled { get; }
    Xbox360PackageInfo InspectPackage(string filePath);
    byte[] ReadEntryBytes(string filePath, string entryPath);
}
