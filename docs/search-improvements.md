# Search improvements — held for the next feature batch

This feature is not released. Keep its PR in draft; do not merge, bump versions,
package a release or publish it until the user requests the next feature batch.

Windows **Tools → Title search** and mobile **Library → Find any game** have
independent platform toggles for Xbox Series X|S, Xbox One, Xbox 360, PC, GFWL,
Win32, Windows 8, Mobile and Other / unknown. All are initially enabled. Select
several to show titles matching any selected platform. All/None controls reset
the selection, and changing filters uses loaded results without a network call.
GFWL editions identified by PC plus Xbox 360 metadata are separated from console
editions. Unknown platform metadata remains accessible through Other / unknown.

Standalone Arabic and Roman installment numbers from 1 to 30 are searched in
both directions: **Black Ops 3** also searches **Black Ops III**. Mixed forms
such as **Final Fantasy 13-2** include **Final Fantasy XIII-2**. Original queries,
names, platform identities and Title IDs are retained; embedded word fragments,
leading-zero numbers and the opening pronoun in **I Am Alive** are unchanged.
Library name filtering also treats these numerals as equivalent on all apps.

Name-query aliases are bounded to eight spellings and merged by Title ID, with
up to 300 discovered results. Distinct edition IDs remain separate. Windows
retains its existing Cyrillic-lookalike fallback when ordinary spellings find
no titles, and its verified-achievements filter works together with platforms.
Individual unavailable spellings retain successful matches with an incomplete
results notice; consecutive transport failures stop further retries.

Mobile decimal Title ID lookups skip the public name-search service and bypass
platform filters until metadata is known. They no longer claim Xbox One before
the platform has been resolved. Lookup does not add games to a profile, change
completion totals or start an unlock/presence session. Existing edition guards
and update-repository URLs remain in place.
