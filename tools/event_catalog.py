"""Read, split, validate and export event catalogs. Never edits an input directory.

python tools/event_catalog.py split SOURCE NEW_OUTPUT --backup-dir F:/UWPDumps/catalog-backup
python tools/event_catalog.py flatten SOURCE NEW_OUTPUT
"""
import argparse
import copy
import json
import re
import shutil
import zipfile
from pathlib import Path

TITLE_KEYS = {'TitleId', 'TitleName', 'Devices', 'ServiceConfigId', 'Enabled', 'FullySupported', 'PartiallySupported', 'Achievements'}
OP_TYPES = {'Replace', 'RangeInt', 'RangeFloat', 'StupidFuckingLDAPTimestamp'}
ID = re.compile(r'[0-9]{1,10}\Z')


def unique(pairs):
    out = {}
    for key, value in pairs:
        if key in out:
            raise ValueError('Duplicate JSON property: ' + key)
        out[key] = value
    return out


def parse(raw):
    return json.loads(raw.decode('utf-8-sig') if isinstance(raw, bytes) else raw, object_pairs_hook=unique)


def dump(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes((json.dumps(data, ensure_ascii=False, indent=2) + '\n').replace('\n', '\r\n').encode('utf-8'))


def template_path(directory, tid):
    return directory / tid / (tid + '.json') if (directory / 'index.json').exists() else directory / (tid + '.json')


def read_index(directory):
    path = directory / ('index.json' if (directory / 'index.json').exists() else 'Data.json')
    data = parse(path.read_bytes())
    if not data or list(data)[-1] != 'SupportedTitleIDs' or not isinstance(data['SupportedTitleIDs'], list):
        raise ValueError('SupportedTitleIDs must be last')
    if any(not ID.fullmatch(str(tid)) for tid in data['SupportedTitleIDs']):
        raise ValueError('Invalid supported title ID')
    return data


def read_title(directory, index, tid):
    if not ID.fullmatch(tid) or tid not in index:
        raise ValueError('Invalid or unlisted title ID')
    if not (directory / 'index.json').exists():
        return index[tid]
    data = parse((directory / tid / 'Data.json').read_bytes())
    if list(data) != [tid, 'SupportedTitleIDs'] or list(map(str, data['SupportedTitleIDs'])) != [tid]:
        raise ValueError('Invalid per-title wrapper: ' + tid)
    if {k: v for k, v in data[tid].items() if k != 'Achievements'} != index[tid]:
        raise ValueError('Index and title file disagree: ' + tid)
    return data[tid]


def read_catalog(directory):
    index = read_index(directory)
    data = {tid: read_title(directory, index, tid) for tid in index if ID.fullmatch(tid)}
    data['SupportedTitleIDs'] = index['SupportedTitleIDs']
    return data


def validate_title(block):
    if not isinstance(block, dict) or set(block) - TITLE_KEYS or not isinstance(block.get('Achievements'), dict):
        raise ValueError('Non-runtime title fields or invalid Achievements')
    for aid, entry in block['Achievements'].items():
        if not ID.fullmatch(aid) or not isinstance(entry, dict) or not entry:
            raise ValueError('Invalid achievement: ' + aid)
        if all(re.fullmatch(r'Request[1-9][0-9]*', k) for k in entry):
            if list(entry) != ['Request' + str(i) for i in range(1, len(entry) + 1)]:
                raise ValueError('Non-contiguous requests')
            requests = entry.values()
        else:
            requests = [entry]
        for request in requests:
            if not isinstance(request, dict) or not request:
                raise ValueError('Empty request')
            for op in request.values():
                if not isinstance(op, dict) or op.get('ReplacementType') not in OP_TYPES or not isinstance(op.get('Target'), str) or not op['Target']:
                    raise ValueError('Invalid replacement operation')
                # The consumer uses JToken.ToString(): numeric replacement values and empty
                # strings are legitimate preexisting payload values, not housekeeping fields.
                if op['ReplacementType'] == 'Replace' and (not isinstance(op.get('Replacement'), (str, int, float)) or isinstance(op['Replacement'], bool)):
                    raise ValueError('Invalid replacement value')


def runtime_title(block):
    result = {k: copy.deepcopy(v) for k, v in block.items() if k in TITLE_KEYS and k != 'Achievements'}
    result['Achievements'] = {}
    for aid, entry in block['Achievements'].items():
        if not isinstance(entry, dict):
            raise ValueError('Scalar achievement mapping: ' + aid)
        direct = {k: v for k, v in entry.items() if isinstance(v, dict) and 'ReplacementType' in v}
        if direct:
            result['Achievements'][aid] = direct
        else:
            requests = [v for k, v in entry.items() if re.fullmatch(r'Request[1-9][0-9]*(?:_.*)?', k) and isinstance(v, dict)]
            result['Achievements'][aid] = {'Request' + str(i): {k: v for k, v in request.items() if isinstance(v, dict) and 'ReplacementType' in v}
                                           for i, request in enumerate(requests, 1)}
    validate_title(result)
    return result


def write_split(source, output, backup):
    if output.exists() or backup.exists():
        raise ValueError('Output and backup must be new directories')
    if output.resolve().is_relative_to(source.resolve()) or backup.resolve().is_relative_to(source.resolve()) or backup.resolve().is_relative_to(output.resolve()):
        raise ValueError('Inputs, outputs and backups must be separate directories')
    data = read_catalog(source)
    cleaned = {tid: runtime_title(block) for tid, block in data.items() if ID.fullmatch(tid)}
    if any(str(tid) not in cleaned for tid in data['SupportedTitleIDs']):
        raise ValueError('Supported ID without a title')
    # Complete backup, including research and original recipes, precedes any generated files.
    backup.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(source, backup)
    output.mkdir(parents=True)
    index = {}
    for tid, block in cleaned.items():
        index[tid] = {k: v for k, v in block.items() if k != 'Achievements'}
        dump(output / tid / 'Data.json', {tid: block, 'SupportedTitleIDs': [tid]})
        shutil.copy2(template_path(source, tid), output / tid / (tid + '.json'))
    index['SupportedTitleIDs'] = data['SupportedTitleIDs']
    dump(output / 'index.json', index)
    for name in ['Stats.json', 'meta.json']:
        if (source / name).exists():
            shutil.copy2(source / name, output / name)
    # Preserve old stat recipes in the separate stats store, outside achievement Data.json.
    stats = parse((output / 'Stats.json').read_bytes()) if (output / 'Stats.json').exists() else {}
    for tid, block in data.items():
        if not ID.fullmatch(tid) or 'Stats' not in block:
            continue
        target = stats.setdefault(tid, {})
        mapped = target.setdefault('Stats', {})
        for key, value in block['Stats'].items():
            if key in mapped and mapped[key] != value:
                raise ValueError('Conflicting existing stat recipe')
            mapped[key] = value
        for key in ['Name', 'TitleName', 'ServiceConfigId', 'StatsMode', 'CandidateReplacementData']:
            if key in block:
                target.setdefault(key, block[key])
    if stats:
        dump(output / 'Stats.json', stats)
    assert read_catalog(output) == {**cleaned, 'SupportedTitleIDs': data['SupportedTitleIDs']}
    print(f'Split {len(cleaned)} titles; source unchanged; originals backed up to {backup}')


def flatten(source, output):
    if output.exists():
        raise ValueError('Output must be a new directory')
    if output.resolve().is_relative_to(source.resolve()):
        raise ValueError('Output must be outside the source directory')
    data = read_catalog(source)
    output.mkdir(parents=True)
    dump(output / 'Data.json', data)
    for tid in data:
        if ID.fullmatch(tid):
            shutil.copy2(template_path(source, tid), output / (tid + '.json'))
    for name in ['Stats.json', 'meta.json']:
        if (source / name).exists():
            shutil.copy2(source / name, output / name)


def reconcile(sources, output, backup):
    if output.exists() or backup.exists():
        raise ValueError('Output and backup must be new directories')
    if any(output.resolve().is_relative_to(source.resolve()) or backup.resolve().is_relative_to(source.resolve()) for source in sources) or backup.resolve().is_relative_to(output.resolve()):
        raise ValueError('Inputs, output and backups must be separate directories')
    data, templates, supported, stats = {}, {}, {}, {}
    for source in sources:
        catalog = read_catalog(source)
        for tid, block in catalog.items():
            if not ID.fullmatch(tid):
                continue
            runtime_title(block)  # Fail before writing if any source recipe cannot be represented.
            data[tid] = block
            templates[tid] = template_path(source, tid)
            if not templates[tid].is_file():
                raise ValueError('Missing standalone template: ' + tid)
        for tid in catalog['SupportedTitleIDs']:
            supported.setdefault(str(tid), tid)
        if (source / 'Stats.json').exists():
            stats.update(parse((source / 'Stats.json').read_bytes()))
    data['SupportedTitleIDs'] = list(supported.values())
    backup.mkdir(parents=True)
    for number, source in enumerate(sources, 1):
        shutil.copytree(source, backup / ('source-' + str(number)))
    staged = backup / 'reconciled-input'
    staged.mkdir()
    dump(staged / 'Data.json', data)
    for tid, path in templates.items():
        shutil.copy2(path, staged / (tid + '.json'))
    if stats:
        dump(staged / 'Stats.json', stats)
    write_split(staged, output, backup / 'reconciled-backup')


def archive(source, output):
    """Package only runtime catalog files; backups and research can never enter the archive."""
    if output.exists():
        raise ValueError('Archive must be a new file')
    index = read_index(source)
    paths = [source / ('index.json' if (source / 'index.json').exists() else 'Data.json')]
    for tid in index:
        if not ID.fullmatch(tid):
            continue
        validate_title(read_title(source, index, tid))
        if (source / 'index.json').exists():
            paths.append(source / tid / 'Data.json')
        paths.append(template_path(source, tid))
    paths += [source / name for name in ['Stats.json', 'meta.json'] if (source / name).exists()]
    for path in paths:
        if not path.is_file() or path.stat().st_size >= 100 * 1024 * 1024:
            raise ValueError('Missing or oversized catalog file: ' + str(path))
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED) as zipped:
        for path in paths:
            zipped.write(path, 'Events/' + path.relative_to(source).as_posix())


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['split', 'flatten', 'reconcile', 'archive'])
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--backup-dir', type=Path)
    parser.add_argument('--sources', type=Path, nargs='+', default=[])
    args = parser.parse_args()
    if args.command in ('split', 'reconcile'):
        if args.backup_dir is None:
            parser.error('split requires --backup-dir')
        if args.command == 'split':
            write_split(args.source, args.output, args.backup_dir)
        else:
            reconcile([args.source, *args.sources], args.output, args.backup_dir)
    elif args.command == 'archive':
        archive(args.source, args.output)
    else:
        flatten(args.source, args.output)
