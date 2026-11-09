"""Build a metadata-only SQLite snapshot; never modify catalog source files."""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import tempfile
import unicodedata
from datetime import datetime, timezone

TITLE_KEYS = {'TitleId', 'TitleName', 'Devices', 'ServiceConfigId', 'Enabled',
              'FullySupported', 'PartiallySupported', 'Achievements'}


def title_id(value):
    value = str(value)
    if not re.fullmatch(r'[1-9][0-9]{0,9}', value) or int(value) > 4294967295:
        raise ValueError('Expected a canonical unsigned 32-bit title ID.')
    return value


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'))


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def normalized(value):
    return unicodedata.normalize('NFKC', value).casefold()


def pairs(items):
    result = {}
    for key, value in items:
        if key in result:
            raise ValueError('Duplicate JSON property: ' + key)
        result[key] = value
    return result


def read(path):
    def reject_constant(value):
        raise ValueError('Non-finite JSON value: ' + value)
    return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=pairs, parse_constant=reject_constant)


def platforms(value):
    if not isinstance(value, list) or any(not isinstance(v, str) or not v.strip() or len(v) > 64 for v in value):
        raise ValueError('Platforms must be a list of nonempty strings.')
    aliases = {'windows pc': 'PC', 'pc': 'PC', 'xbox one': 'XboxOne', 'xboxone': 'XboxOne',
               'xbox series x|s': 'XboxSeries', 'xbox series': 'XboxSeries', 'xboxseries': 'XboxSeries',
               'xbox 360': 'Xbox360', 'xbox360': 'Xbox360', 'windows 8': 'Windows8', 'windows8': 'Windows8',
               'gfwl': 'GFWL', 'win32': 'Win32', 'mobile': 'Mobile'}
    return sorted({aliases.get(normalized(v.strip()), v.strip()) for v in value})


def project(repo):
    """Return only explicitly selected public fields, never replacement operations."""
    from tools.event_catalog import validate_title
    repo = Path(repo)
    events = repo / 'Events'
    index = read(events / 'index.json')
    if not isinstance(index, dict) or list(index)[-1:] != ['SupportedTitleIDs'] or not isinstance(index['SupportedTitleIDs'], list):
        raise ValueError('SupportedTitleIDs must be the final index property.')
    listed = [title_id(v) for v in index['SupportedTitleIDs']]
    if len(set(listed)) != len(listed) or set(listed) - (index.keys() - {'SupportedTitleIDs'}):
        raise ValueError('Duplicate or absent supported title ID.')
    listed = set(listed)
    public = read(repo / 'site/assets/games.json')
    names = {}
    for item in public:
        key = title_id(item['titleId'])
        if key in names or not isinstance(item.get('name'), str) or not item['name'].strip():
            raise ValueError('Invalid public title name.')
        names[key] = item

    definitions = read(repo / 'catalog/achievement-totals.json')
    if definitions.get('schemaVersion') != 1 or not isinstance(definitions.get('entries'), list):
        raise ValueError('Unsupported totals schema.')
    totals = {}
    for entry in definitions['entries']:
        key = title_id(entry['titleId'])
        total = entry.get('total')
        checked = entry.get('checkedAt')
        if key in totals or type(total) is not int or not 0 < total <= 10000 or not isinstance(checked, str):
            raise ValueError('Invalid definition total.')
        timestamp = datetime.fromisoformat(checked.replace('Z', '+00:00'))
        if timestamp.tzinfo is None or timestamp > datetime.now(timezone.utc):
            raise ValueError('Invalid definition check time.')
        totals[key] = {'titleId': key, 'total': total, 'platforms': platforms(entry['platforms']), 'checkedAt': checked}

    titles = []
    for key, summary in index.items():
        if key == 'SupportedTitleIDs':
            continue
        title_id(key)
        document = read(events / key / 'Data.json')
        if list(document) != [key, 'SupportedTitleIDs'] or list(map(title_id, document['SupportedTitleIDs'])) != [key]:
            raise ValueError('Invalid per-title catalog ordering or identity.')
        block = document[key]
        if not isinstance(block, dict) or set(block) - TITLE_KEYS or not isinstance(block.get('Achievements'), dict):
            raise ValueError('Invalid runtime title block.')
        validate_title(block)
        for field in ('Enabled', 'FullySupported', 'PartiallySupported'):
            if field in block and type(block[field]) is not bool:
                raise ValueError('Invalid runtime boolean: ' + field)
        if 'TitleId' in block and title_id(block['TitleId']) != key:
            raise ValueError('Runtime title identity mismatch.')
        if {k: v for k, v in block.items() if k != 'Achievements'} != summary:
            raise ValueError('Index and title file disagree: ' + key)
        devices = platforms(block.get('Devices', []))
        if not devices and isinstance(names.get(key, {}).get('platform'), str):
            devices = platforms([p.strip() for p in names[key]['platform'].split(',') if p.strip()])
        path = events / key / (key + '.json')
        template = path.read_bytes() if path.exists() else None
        active = key in listed and block.get('Enabled') is not False
        if active and template is None:
            raise ValueError('Active title template is missing: ' + key)
        available = active and bool(block['Achievements'])
        reason = 'unlisted' if key not in listed else 'disabled' if not active else 'no-mappings' if not available else None
        status = ('Unsupported' if not active else 'Supported' if block.get('FullySupported') is True
                  else 'PartiallySupported' if block.get('PartiallySupported') is True else 'Testing')
        name = names.get(key, {}).get('name') or block.get('TitleName') or key
        if not isinstance(name, str) or len(name) > 1024:
            raise ValueError('Invalid title name.')
        aliases = sorted({block['TitleName']} - {name}) if isinstance(block.get('TitleName'), str) else []
        definition = totals.get(key)
        if definition and devices and not {normalized(v) for v in devices} & {normalized(v) for v in definition['platforms']}:
            definition = None
        # Replacement objects execute in insertion order; sorting their keys would
        # hide a behavioral change from mappingRevision.
        runtime = json.dumps(block, ensure_ascii=False, separators=(',', ':')).encode()
        mapping_revision = hashlib.sha256(runtime + b'\0' + (template if template is not None else b'missing') + b'\0' + str(key in listed).encode()).hexdigest()
        titles.append({'titleId': key, 'name': name, 'aliases': aliases, 'platforms': devices,
                       'status': status, 'eventAvailable': available, 'availabilityReason': reason,
                       'mappedAchievementCount': len(block['Achievements']),
                       'definitionAchievementCount': definition['total'] if definition else None,
                       'definitionCheckedAt': definition['checkedAt'] if definition else None,
                       'mappingRevision': mapping_revision})
    return sorted(titles, key=lambda r: r['titleId']), sorted(totals.values(), key=lambda r: r['titleId'])


def build(repo, output, source_revision):
    repo, output = Path(repo).resolve(), Path(output).resolve()
    if output.suffix.lower() != '.sqlite' or any(output.is_relative_to((repo / folder).resolve())
            for folder in ('Events', 'src/AchievementLabs.App/Events', 'catalog', 'site')):
        raise ValueError('Write the .sqlite snapshot outside catalog source directories.')
    titles, totals = project(repo)  # Validate every input before touching the destination.
    metadata = {'schemaVersion': 1, 'catalogRevision': digest({'titles': titles, 'totals': totals}),
                'sourceRevision': source_revision, 'scope': 'event-catalog',
                'titleCount': len(titles), 'totalsCount': len(totals)}
    if not isinstance(source_revision, str) or not re.fullmatch(r'[a-f0-9]{40}(?:-dirty)?', source_revision):
        raise ValueError('Source revision must be a Git SHA, optionally suffixed -dirty.')
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary = tempfile.mkstemp(prefix=output.name + '.', suffix='.tmp', dir=output.parent)
    os.close(handle)
    try:
        with closing(sqlite3.connect(temporary)) as db, db:
            db.executescript('''
                CREATE TABLE metadata (document TEXT NOT NULL);
                CREATE TABLE titles (title_id TEXT PRIMARY KEY, sort_name TEXT NOT NULL,
                    search_text TEXT NOT NULL, status TEXT NOT NULL, document TEXT NOT NULL);
                CREATE INDEX title_order ON titles(sort_name,title_id);
                CREATE INDEX title_status ON titles(status);
                CREATE TABLE title_platforms (title_id TEXT NOT NULL, platform TEXT NOT NULL,
                    PRIMARY KEY(title_id,platform));
                CREATE INDEX platform_filter ON title_platforms(platform,title_id);
                CREATE TABLE totals (title_id TEXT PRIMARY KEY, document TEXT NOT NULL);
            ''')
            db.execute('INSERT INTO metadata VALUES(?)', (canonical(metadata),))
            for record in titles:
                db.execute('INSERT INTO titles VALUES(?,?,?,?,?)', (record['titleId'], normalized(record['name']),
                    normalized(' '.join([record['name'], record['titleId'], *record['aliases']])), record['status'], canonical(record)))
                db.executemany('INSERT INTO title_platforms VALUES(?,?)', [(record['titleId'], normalized(p)) for p in record['platforms']])
            db.executemany('INSERT INTO totals VALUES(?,?)', [(r['titleId'], canonical(r)) for r in totals])
            if db.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
                raise ValueError('Snapshot integrity check failed.')
        os.replace(temporary, output)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    return metadata


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path('.'))
    parser.add_argument('--output', type=Path, required=True)
    options = parser.parse_args()
    revision = subprocess.check_output(['git', '-C', str(options.repo), 'rev-parse', 'HEAD'], text=True).strip()
    if subprocess.check_output(['git', '-C', str(options.repo), 'status', '--porcelain', '--', 'Events', 'catalog/achievement-totals.json', 'site/assets/games.json'], text=True).strip():
        revision += '-dirty'
    print(canonical(build(options.repo, options.output, revision)))


if __name__ == '__main__':
    main()
