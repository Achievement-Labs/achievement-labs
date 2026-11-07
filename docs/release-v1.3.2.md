# Achievement Labs v1.3.2

Windows maintenance release combining PRs #46–49.

- Attach to the updated Xbox app, including XboxPcAppCE, using read-only process access and improved session detection and diagnostics.
- Compress bundled event data with Brotli and remove the unused Mono.Cecil runtime dependency.
- Restore stats editor loading of bundled definitions and templates when no local Events folder is configured.
- Refresh 115 existing recipe blocks and 140 templates, including PFN, service configuration, and event-ID corrections; add 15 titles.
- Bundle 576 event titles: 353 Supported, 89 Partially Supported, and 134 Testing. Existing repository support classifications are preserved.

The catalog was validated through the actual offline consumer: 25,830 mappings and 177,860 payloads, with zero rendering failures. Offline validation does not establish unlock success. No live telemetry or unlock tests were performed.

Download AchievementLabs.exe. Existing Windows installations can use the normal updater. Events-Indexed.zip is supplied for separate catalog updates; a manual Events download is not required for the built-in catalog.

The API/hosting redesign and mobile cloud sync are outside this maintenance release. Native preview versions and mobile update destinations are unchanged.
