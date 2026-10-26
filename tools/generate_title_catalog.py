"""Regenerate the public title lists from Events/Data.json.

Writes docs/title-support.md, Events/README.md and site/assets/games.json using the
same eligibility rules as tools/Export-StripeGameCatalog.ps1: a title is available when
it is not disabled, is listed in SupportedTitleIDs and has a standalone template.
Available + FullySupported is Supported, available otherwise is Testing, anything
else is Coming Soon. Read-only for Data.json.

Usage: python tools/generate_title_catalog.py [release-label]
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RELEASE = sys.argv[1] if len(sys.argv) > 1 else 'the next release'
MANUAL = {
    '571417442': ('Gigantic', ['PC']),
    '908546367': ('Songbringer', ['PC']),
    '1835298427': ('Minecraft', ['Apple TV']),
}
DEVICE_LABELS = {'PC': 'Windows PC', 'Windows': 'Windows PC', 'Desktop': 'Windows PC',
                 'XboxOne': 'Xbox One', 'XboxSeries': 'Xbox Series X|S', 'Xbox360': 'Xbox 360'}
ORDER = ['Supported', 'Testing', 'Coming Soon']


def platform(devices):
    labels = []
    for d in devices or []:
        label = DEVICE_LABELS.get(str(d), str(d))
        if label and label not in labels:
            labels.append(label)
    return ', '.join(labels)


def cell(text):
    return str(text).replace('|', '\\|')


def rows():
    data = json.loads((ROOT / 'Events/Data.json').read_text(encoding='utf-8'))
    assert list(data)[-1] == 'SupportedTitleIDs', 'SupportedTitleIDs must be the last key'
    xbox = {str(e['TitleId']): e for e in json.loads((ROOT / 'XboxTitleIDs.json').read_text(encoding='utf-8'))}
    listed = {str(x) for x in data['SupportedTitleIDs']}
    out = []
    for tid, block in data.items():
        if not tid.isdigit():
            continue
        record = xbox.get(tid, {})
        manual = MANUAL.get(tid, (None, None))
        name = block.get('TitleName') or record.get('Name') or block.get('Name') or manual[0] or f'Title {tid}'
        devices = block.get('Devices') or record.get('Devices') or manual[1]
        available = (block.get('Enabled') is not False and tid in listed
                     and (ROOT / 'src/AchievementLabs.App/Events' / f'{tid}.json').exists())
        status = 'Supported' if available and block.get('FullySupported') else 'Testing' if available else 'Coming Soon'
        mapped = sum(1 for v in (block.get('Achievements') or {}).values() if v)
        out.append(dict(id=tid, name=name, platform=platform(devices), status=status, mapped=mapped,
                        template=(ROOT / 'Events' / f'{tid}.json').exists()))
    out.sort(key=lambda r: (r['name'].casefold(), r['platform'], r['id']))
    return out


def summary(rs):
    counts = {s: sum(r['status'] == s for r in rs) for s in ORDER}
    return f"{len(rs)} titles: " + ', '.join(f"**{counts[s]} {s}**" for s in ORDER)


def write(path, text):
    (ROOT / path).write_text(text.replace('\n', '\r\n'), encoding='utf-8', newline='')


def main():
    rs = rows()
    lines = ['# Complete event title catalog', '',
             summary(rs) + '. This list covers event mappings; Xbox 360, GFWL, Steam, and other app tools use separate workflows.', '',
             'Supported classifications reflect the author’s corrections. Testing includes partial or still-under-test mappings; mapped counts do not indicate the game’s total achievements.']
    for s in ORDER:
        lines += ['', f'## {s}', '', '| Title | Platform | Title ID | Mapped achievements |', '|---|---|---|---:|']
        lines += [f"| {cell(r['name'])} | {cell(r['platform'])} | {r['id']} | {r['mapped']} |" for r in rs if r['status'] == s]
    write('docs/title-support.md', '\n'.join(lines) + '\n')

    lines = ['# Event title catalogue', '',
             summary(rs) + f'. This is the author-maintained catalogue included in {RELEASE} and synchronized to the hosted catalogue.', '',
             'Classifications follow the author’s data. Mapped counts describe recipes, not the full achievement list. Xbox 360, GFWL and Steam use separate workflows.', '',
             '| Title | Platform | Title ID | Mapped achievements | Status | Template |', '|---|---|---|---|---|---|']
    lines += [f"| {cell(r['name'])} | {cell(r['platform'])} | `{r['id']}` | {r['mapped']} | {r['status']} | "
              + (f"[{r['id']}.json]({r['id']}.json)" if r['template'] else 'Unavailable') + ' |' for r in rs]
    write('Events/README.md', '\n'.join(lines) + '\n')

    status = {'Supported': 'supported', 'Testing': 'testing', 'Coming Soon': 'coming-soon'}
    games = [dict(titleId=r['id'], name=r['name'], platform=r['platform'], status=status[r['status']]) for r in rs]
    write('site/assets/games.json', json.dumps(games, indent=2, ensure_ascii=False))
    print(summary(rs))


if __name__ == '__main__':
    main()
