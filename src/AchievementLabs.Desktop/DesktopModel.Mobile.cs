using AchievementLabs.Core;
namespace AchievementLabs.Desktop;
public sealed partial class DesktopModel
{
    public MobileTransferFile CreateMobileTransfer(bool eventOnly=false, bool signInOnly=false)
    {
        if (session == null) throw new InvalidOperationException("Connect Xbox first.");
        return MobileTransfer.Create(session.Authorization,session.Xuid,session.EventsToken,session.AuthorizationExpiresAt,eventOnly,signInOnly);
    }
}

