using AchievementLabs.Core;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    private EventCatalogClient? activeEventCatalog;
    private EventCatalogClient eventCatalog => activeEventCatalog ??= new(true, () => EventsDirectory);
}
