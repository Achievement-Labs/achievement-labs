# Project instructions

## Data.json ordering — explicit user requirement

- All game/title blocks must appear ABOVE `SupportedTitleIDs` in every working and live Events/Data.json file.
- Keep `SupportedTitleIDs` as the final top-level property. Never append a title below it.
- After every Data.json edit or generation, verify that `SupportedTitleIDs` is last and that zero numeric title keys occur below it before publishing.
- Reordering must preserve every title block, achievement mapping, metadata value, and supported ID. Back up the affected files and preserve unrelated user edits.
- Apply this rule to scripts that generate or update Data.json as well as direct edits. Existing preparation scripts may append keys; do not assume they satisfy this rule.

## Event testing templates — explicit user requirement

- Use a standalone Events/<titleId>.json template for every test title, following the existing ordinary replacement flow.
- Do not generate RawBody or TemplateReplacement operations or embed full templates in achievement recipes.
- Keep test patterns simple. Exclude randomized multi-stat patterns involving map IDs, kills, mission scores, time played, multipliers and LDAP timestamps.
- Preserve preexisting unrelated title recipes when enforcing these rules on new testing work.

## Repository routing

App updates (Windows, Android, iOS) and event JSON both come from `Achievement-Labs/achievement-labs`. Never point an official build's update feed at a fork.

The user authorized bundling the reusable Horizon-sourced CON identity after the complete 556-byte build record was verified to match Horizon, provided no personal profile or sign-in data is included. Keep raw build inputs, personal profiles, custom keyvaults and account secrets excluded from Git/releases, and never print signing values.
