"""Inventory title-achievement/event-stat games and add missing stat-only infrastructure.

Never derives event names, stat fields, values, or achievement recipes from classification.
Existing files are preserved; an existing aggregate is extended only for new stat titles.
"""
import argparse
import csv
import json
import re
import shutil
import sqlite3
import uuid
from pathlib import Path


def encode(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + '\n').replace('\n', '\r\n').encode('utf-8')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def identities(database):
    with sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True) as connection:
        connection.row_factory = sqlite3.Row
        rows = [dict(row) for row in connection.execute(
            "SELECT TitleId,Name,ServiceConfigId,PackageFamilyName,AchievementsType,StatsType,StatsTypeSource "
            "FROM FilteredTitle WHERE AchievementsType='Title Based' AND StatsType='Event Based' ORDER BY TitleId")]
        all_titles = {row['TitleId']: dict(row) for row in connection.execute(
            'SELECT TitleId,Name,ServiceConfigId,PackageFamilyName FROM CatalogTitle')}
        all_titles.update({row['TitleId']: row for row in rows})
    return rows, all_titles


def folder(root, tid, name, named):
    numeric = root / tid
    matches = list(root.glob(tid + ' - *'))
    if numeric.is_dir():
        return numeric
    if len(matches) > 1:
        raise ValueError(f'Duplicate folders for {tid}')
    if matches:
        return matches[0]
    safe = re.sub(r'[<>:"/\\|?*\x00-\x1f]', ' - ', name).strip(' .')[:150]
    return root / (tid + ' - ' + safe if named else tid)


def template(tid, identity):
    pfn = identity.get('PackageFamilyName')
    if not pfn:
        raise ValueError(f'{tid}: package identity unavailable; refusing a donor template')
    # App is a conventional envelope default, not a recovered/verified application ID.
    # Its provenance is reported outside runtime files; no write recipe is invented.
    value = {
        'ver': '4.0', 'name': f'Microsoft.XboxLive.T{tid}.REPLACEEVENTNAME',
        'time': 'REPLACETIME', 'iKey': 'o:0890af88a9ed4cc886a14f5e174a2827',
        'ext': {
            'utc': {'shellId': 1, 'eventFlags': 1, 'pgName': 'XBOX', 'flags': 1, 'epoch': '1', 'seq': 'REPLACESEQ'},
            'privacy': {'dataType': 1, 'isRequired': False}, 'metadata': 'REPLACEMETADATA',
            'app': {'id': 'U:' + pfn + '!App'},
        },
        'data': 'REPLACEDATA',
    }
    text = encode(value).decode('utf-8')
    for token in ('REPLACESEQ', 'REPLACEMETADATA', 'REPLACEDATA'):
        text = text.replace('"' + token + '"', token)
    return text.encode('utf-8')


def prepare(root, rows, all_titles, named=False, template_source=None):
    for row in rows:
        tid = row['TitleId']
        if not re.fullmatch(r'[1-9][0-9]*', tid) or int(tid) > 4294967295:
            raise ValueError('Invalid title identity: ' + tid)
        uuid.UUID(row['ServiceConfigId'])
    aggregate_path = root / 'Stats.json'
    aggregate = read(aggregate_path) if aggregate_path.exists() else None
    planned, inventory = {}, []
    scoped = {row['TitleId']: row for row in rows}
    # Also repair missing templates for stat folders already present, without changing recipes.
    existing = {}
    for path in root.glob('*/Stats.json'):
        for tid, block in read(path).items():
            if tid.isdecimal() and isinstance(block, dict):
                if tid in existing:
                    raise ValueError(f'Duplicate stat title {tid}')
                existing[tid] = (path.parent, block)
                scoped.setdefault(tid, all_titles.get(tid, {'TitleId': tid, 'Name': path.parent.name}))
    for tid, identity in sorted(scoped.items()):
        target, block = existing.get(tid, (None, None))
        if target is None:
            target = folder(root, tid, identity['Name'], named)
            block = aggregate.get(tid) if aggregate is not None else None
            if block is None:
                block = {'TitleName': identity['Name'], 'ServiceConfigId': identity['ServiceConfigId'],
                         'StatsMode': 'Event', 'Stats': {}}
            planned[target / 'Stats.json'] = encode({tid: block, 'SupportedTitleIDs': [tid]})
        if aggregate is not None and tid not in aggregate:
            aggregate[tid] = block
        output = target / (tid + '.json')
        source = 'existing-standalone'
        if not output.exists():
            if block.get('StatTemplate'):
                text = block['StatTemplate']
                planned[output] = text.replace('\r\n', '\n').replace('\n', '\r\n').encode('utf-8')
                source = 'existing-inline-StatTemplate'
            else:
                prior = folder(template_source, tid, identity['Name'], True) / (tid + '.json') if template_source else None
                if prior and prior.exists() and identity.get('PackageFamilyName'):
                    planned[output] = prior.read_bytes()
                    source = 'existing-standalone:' + str(prior)
                elif identity.get('PackageFamilyName'):
                    planned[output] = template(tid, identity)
                    source = 'catalog-PFN-envelope-App-default-unverified'
                else:
                    source = 'missing-package-identity-no-template-generated'
        mapped = sum(any(re.fullmatch(r'Request\d+', key) for key in stat)
                     for stat in block.get('Stats', {}).values() if isinstance(stat, dict))
        if tid in {row['TitleId'] for row in rows} or not output.exists():
            inventory.append({'TitleId': tid, 'Title': identity['Name'],
                              'AchievementsType': identity.get('AchievementsType', ''),
                              'StatsType': identity.get('StatsType', ''),
                              'StatsTypeSource': identity.get('StatsTypeSource', ''),
                              'ServiceConfigId': identity.get('ServiceConfigId', ''),
                              'PackageFamilyName': identity.get('PackageFamilyName', ''),
                              'Folder': str(target), 'TemplateSource': source,
                              'ExistingStatDefinitions': len(block.get('Stats', {})),
                              'ExistingEventRecipes': mapped,
                              'RuntimeVerified': False})
    if aggregate is not None:
        encoded = encode(aggregate)
        if read(aggregate_path) != aggregate:
            planned[aggregate_path] = encoded
    return planned, inventory


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', required=True, type=Path)
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--named', action='store_true')
    parser.add_argument('--template-source', type=Path)
    parser.add_argument('--inventory', required=True, type=Path)
    parser.add_argument('--backup', type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    rows, all_titles = identities(args.database)
    planned, inventory = prepare(args.root, rows, all_titles, args.named, args.template_source)
    args.inventory.parent.mkdir(parents=True, exist_ok=True)
    with args.inventory.open('w', encoding='utf-8-sig', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=list(inventory[0]))
        writer.writeheader(); writer.writerows(inventory)
    if args.apply:
        if args.backup is None:
            parser.error('--apply requires --backup')
        args.backup.mkdir(parents=True, exist_ok=False)
        manifest = []
        for path in planned:
            exists = path.exists()
            manifest.append({'path': str(path.resolve()), 'existed': exists})
            if exists:
                backup = args.backup / path.relative_to(args.root)
                backup.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(path, backup)
        (args.backup / 'manifest.json').write_bytes(encode(manifest))
        for path, content in planned.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
    print(json.dumps({'classifiedTitles': len(rows), 'inventoryRows': len(inventory),
                      'newOrUpdatedFiles': len(planned), 'applied': args.apply,
                      'newWriteRecipes': 0, 'runtimeVerifiedTitles': 0}))


if __name__ == '__main__':
    main()
