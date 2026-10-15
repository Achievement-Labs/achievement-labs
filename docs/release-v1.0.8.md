# Achievement Labs v1.0.8 — bug fixes

This patch includes all three pull requests merged after v1.0.7. Download the updated AchievementLabs.exe, or use the in-app update check. Existing settings and saved event tokens are retained.

## Event-token validation — [PR #5](https://github.com/ethanwp28/achievement-labs/pull/5)

- Fix valid pasted event tokens being refused as belonging to a different account when the attached session's user hash cannot be read.
- Recognize quoted, case-insensitive and nonnumeric user hashes. Redacted hashes remain unknown rather than being treated as proof of an account mismatch.
- Save a well-formed, non-expired token when a readable hash differs, with an explanatory warning. Remove the repeated user-hash gate before event actions; Xbox remains responsible for accepting the token.
- Retain token format/expiry checks, protected Windows-user storage, and blocking of tokens already rejected by Xbox. Event-token entry remains manual; automatic token capture is not included.

## Event templates and catalogue — [PR #7](https://github.com/ethanwp28/achievement-labs/pull/7)

| Title | Title ID | Change |
|---|---|---|
| The Evil Within 2 | `170773198` | Correct the event to `AchievementUnlockBERO` and the UnlockId placeholder to `REPLACEID`. |
| The Evil Within 2, alternate edition | `452142470` | Apply the same event-name and UnlockId template fixes. |
| Rocket League | `558797228` | Replace the single-recipe template with the merged event-name/metadata/data flow and 77 recipes. Author support flags are retained. |
| FRU | `1013213714` | Add a standalone template and 16 recipes. Testing classification is retained. |
| GoldenEye 007 | `1766089184` | Add a standalone template and 49 catalogue recipes. The title remains disabled; this does not announce working unlock support. |
| Rare Replay | `515050978` | Add a standalone template and catalogue block. The title remains disabled with no achievement recipes. |

Both repository Events copies, the hosted catalogue and the executable use the merged catalogue, with **445 numeric title blocks: 267 Supported, 170 Testing and 8 Coming Soon** and SupportedTitleIDs as the final top-level property. The matching Events.zip and title/status CSV are included. These updates do not promote disabled or Testing titles to Supported.

## Catalogue documentation — [PR #6](https://github.com/ethanwp28/achievement-labs/pull/6)

- Escape the pipe in Xbox Series X\|S platform labels so the catalogue table keeps its six columns and the title IDs, counts, status and template links align correctly.
- Refresh public catalogue metadata and release assets to include the merged entries.

## Credits and validation

Thanks to the authors and contributors of PRs #5, #6 and #7. The feature credits from [v1.0.7](https://github.com/ethanwp28/achievement-labs/releases/tag/v1.0.7), including [@thevampirebat](https://github.com/thevampirebat)'s fork contributions, continue to apply.

Synthetic regression checks cover hash parsing, unreadable/matching/different hashes, copied-header normalization, and the use-time validation regression while retaining expiry, format and Xbox-rejection checks. No real credentials are read or overwritten by these checks. Catalogue payload structure/title identity and packaged Windows startup are checked separately; live Xbox achievement credit is not independently verified by an offline test.

Validation passed: token regression checks, UI/scrolling checks, **436 local routes / 17,927 usable recipes / 60,187 strict JSON payloads**, native packaged startup and maximized-window checks. All **449 embedded catalogue resources** match the merged source byte for byte. The server catalogue was imported after a database backup and passed integrity/foreign-key checks.
