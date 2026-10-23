# Find games outside your profile

On Android or iOS, open **Library > Find any game**. Enter a game name or a
decimal Xbox Title ID, tap **Search all games**, and select the correct game and
platform to open its achievements. Xbox sign-in is required to open account
progress. From the achievement page, the existing Auto Unlock and Title Spoofing
controls work as before; lookup does not start either session.

Name search uses the same public DBox title catalogue as the PC version and shows
up to 100 results. Refine broad searches to find a particular edition. The normal
Library search continues to filter your own profile; Find any game searches
independently. A decimal Title ID bypasses the public name-search service.

Discovery does not add a game to your Xbox profile or alter library completion
totals. Public search requests contain the search text and no Xbox account
credentials. Xbox reads happen only after a result is selected. If TitleHub has
no history entry, public name/platform metadata remains available while the app
requests achievements for that exact Title ID. No progress is invented when Xbox
returns no achievements. Some games require being launched before Xbox exposes
account achievement data. Search results do not establish event support; existing
edition restrictions and mobile read-only handling for Xbox 360/GFWL remain.

The new versions are Android 0.6.3 (11) and iOS 0.12.5 (23). CI includes parser,
ID-boundary, query-encoding, public credential-isolation and title-identity checks,
plus offline discovery screen previews on both mobile platforms.
