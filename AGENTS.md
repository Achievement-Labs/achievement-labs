# Project instructions

## Data.json ordering — explicit user requirement

- All game/title blocks must appear ABOVE `SupportedTitleIDs` in every working and live Events/Data.json file.
- Keep `SupportedTitleIDs` as the final top-level property. Never append a title below it.
- After every Data.json edit or generation, verify that `SupportedTitleIDs` is last and that zero numeric title keys occur below it before publishing.
- Reordering must preserve every title block, achievement mapping, metadata value, and supported ID. Back up the affected files and preserve unrelated user edits.
- Apply this rule to scripts that generate or update Data.json as well as direct edits. Existing preparation scripts may append keys; do not assume they satisfy this rule.

## Event testing templates — explicit user requirement

- New repository catalogs use Events/index.json and Events/<titleId>/Data.json plus Events/<titleId>/<titleId>.json. Legacy flat catalogs remain readable. Keep each title in its own folder and retain the ordinary replacement flow.
- Per-title Data.json contains exactly its numeric title block followed by SupportedTitleIDs. The index contains runtime title summaries without achievement mappings, with SupportedTitleIDs last. Never put research or diagnostic fields in either file.
- Do not generate RawBody or TemplateReplacement operations or embed full templates in achievement recipes.
- Keep test patterns simple. Exclude randomized multi-stat patterns involving map IDs, kills, mission scores, time played, multipliers and LDAP timestamps.
- Preserve preexisting unrelated title recipes when enforcing these rules on new testing work.

## Repository routing

Windows app updates and event JSON come from `Achievement-Labs/achievement-labs`. Android/iOS source and future app updates live in `Achievement-Labs/achievement-labs-mobile`; existing released mobile clients retain their original update endpoint until a bridge release. Keep the public mobile event feed here for both generations of clients. Never point an official build's update feed at a fork.

The user authorized bundling the reusable Horizon-sourced CON identity after the complete 556-byte build record was verified to match Horizon, provided no personal profile or sign-in data is included. Keep raw build inputs, personal profiles, custom keyvaults and account secrets excluded from Git/releases, and never print signing values.

## Desktop platform builds

Keep Windows as the existing default target and EXE build. Mac (`LabsMacOS=true`) and Linux (`LabsLinux=true`) produce separate executables/packages in this repository. Platform-specific authentication, storage, notification and update handling must not change Windows behavior. Do not silently route Linux through Windows-only APIs or select Windows update assets on another OS. Desktop build helpers belong under tools/, independent of the mobile repository. Native CI uses synthetic credentials; live telemetry or unlock testing requires explicit user authorization.
