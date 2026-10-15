# Pre-release polish

These changes are local preparation only. Publishing a release requires the owner's explicit approval.

- Fresh settings now default to maximized startup, with the saved preference applied before the window first displays and reapplied after monitor placement. An explicit saved AutoMaximize=false is respected. A native Windows check verifies initial and restored WindowState are both Maximized.
- Achievement sorting includes Global % low-to-high and high-to-low. Unknown rarity stays last; zero is a known value. DLC sections remain grouped.
- The Legacy tools page/navigation is removed. Xbox 360 profiles, GFWL and existing legacy achievement-reading support remain available through their dedicated workflows.
- Event tokens use manual entry in Settings, with secure storage and expiry checks. Automatic retrieval controls and background retrieval have been removed.
- The repository's bot code, operational documentation and bot-specific ignore entries have been removed from the working tree. Local bot files/data were archived outside the repository; Git history was not rewritten.

For v1.0.7 the owner explicitly authorized synchronizing the current F-drive catalogue into both repo copies and the release. Review Data.json and Stats.json along with the corresponding standalone title templates, validate payloads and retain SupportedTitleIDs as the last property. An optional Events.zip can be a separate download for manual/offline use, while the approved app build can retain its tested embedded fallback snapshot. The automatic additions updater does not replace existing title mappings, so corrections to existing titles still need a reviewed catalogue/app update. Do not include backups, token files, research dumps or unrelated F-drive files in the catalogue PR or archive.
