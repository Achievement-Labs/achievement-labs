# Update repository audit

All production update sources target `Achievement-Labs/achievement-labs`:

| Check | Source |
| --- | --- |
| Windows app | Official GitHub releases list; matching `update.json` and `AchievementLabs.exe` assets |
| Android app | Official GitHub releases list; newest matching Android APK |
| iOS app | Official GitHub releases list; newest matching unsigned iOS IPA |
| Windows event data | Official stable release containing `Events.zip` |
| Android/iOS event data | Official `mobile-event-feed/catalogue.json` branch |
| Event feed publisher | Official stable release containing `Events.zip` |
| Shared totals/DLC/hubs | Official repository catalogue files |

App checks select their own platform. A newer mobile-only release cannot hide
the Windows executable or the event archive. Windows chooses the highest
eligible manifest revision and accepts existing stable tags, full four-part
version tags and historical fork revision tags only when assets come from the
official repository. Event sync retains release SHA-256, archive, template and
cache validation. Android retains APK hash and installed signer verification.
No automatic update request carries Xbox authorization or event-token headers.

The update URL and event-feed tests assert the official repository explicitly;
old-repository app downloads are rejected. The mobile public feed has been
initialized. Its scheduled publisher starts after the migration workflow reaches
the official default branch.

Previously installed apps retain their embedded addresses until updated once.
The iOS app checks and links to new IPA releases; installing still requires user
signing through Signulous or the user's chosen signing method. Android and
Windows retain their existing installer/restart controls.
