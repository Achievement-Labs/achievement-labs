"""Build editable stat recipes from a title's Hero stat keys and event envelope."""
import argparse
import json
import re
from pathlib import Path

from event_catalog import dump, parse

TOKEN = re.compile(r'"(?:[^"\\]|\\.)*"|REPLACE[A-Z0-9_]+')
COMMON = {'REPLACETIME', 'REPLACESEQ', 'REPLACEXUID', 'REPLACESESSIONGUID'}
SLOT = 'STATPAYLOADINPUT'


def envelope(raw):
    # Parse the existing title envelope without treating template tokens as JSON.
    raw = re.sub(r'(?m)^([ \t]*)(REPLACE[A-Z0-9_]+)[ \t]*(,?)\s*$', r'\1"statTemplateTemporary": "\2",', raw)
    raw = re.sub(r'"(?:[^"\\]|\\.)*"|,(?=\s*[}\]])', lambda m: m[0] if m[0].startswith('"') else '', raw)
    obj = json.loads(TOKEN.sub(lambda m: m[0] if m[0].startswith('"') else json.dumps(m[0]), raw))
    obj['name'] = 'REPLACEEVENTNAME'
    obj['data'] = 'REPLACEDATA'
    if not isinstance(obj.get('ext', {}), dict):
        raise ValueError('Envelope needs an ext replacement')
    obj.setdefault('ext', {}).pop('statTemplateTemporary', None)
    obj['ext']['metadata'] = 'REPLACEMETADATA'
    def clean(value):
        if isinstance(value, str):
            for token in set(re.findall(r'REPLACE[A-Z0-9_]+', value)) - COMMON - {'REPLACEEVENTNAME', 'REPLACEMETADATA', 'REPLACEDATA'}:
                value = value.replace(token, '')
        elif isinstance(value, dict):
            return {k: clean(v) for k, v in value.items()}
        elif isinstance(value, list):
            return [clean(v) for v in value]
        return value
    result = json.dumps(clean(obj), ensure_ascii=False, separators=(',', ':'))
    for token in ('REPLACEDATA', 'REPLACEMETADATA', 'REPLACESEQ'):
        result = result.replace(json.dumps(token), token)
    return result


def rendered_envelope(raw, title_file, tid):
    for mapping in parse(title_file.read_bytes())[tid]['Achievements'].values():
        requests = mapping.values() if 'Request1' in mapping else [mapping]
        for request in requests:
            body = raw
            for op in request.values():
                if isinstance(op, dict) and op.get('ReplacementType') == 'Replace':
                    body = body.replace(op['Target'], str(op['Replacement']))
            try:
                return envelope(body)
            except (ValueError, TypeError):
                pass
    raise ValueError('No reusable rendered envelope')


def op(target, value):
    return {'ReplacementType': 'Replace', 'Target': target, 'Replacement': value}


def known_payloads(raw, title_file, tid):
    if not title_file.is_file():
        return {}
    meanings = {}
    mappings = parse(title_file.read_bytes())[tid].get('Achievements', {})
    for mapping in mappings.values():
        if not isinstance(mapping, dict):
            continue
        requests = mapping.values() if 'Request1' in mapping else [mapping]
        for request in requests:
            if not isinstance(request, dict):
                continue
            for operation in request.values():
                if not isinstance(operation, dict) or operation.get('Target') != 'REPLACEDATA':
                    continue
                try:
                    data = json.loads(operation['Replacement'])
                    base = data['baseData']
                    event = base['name']
                    fields = tuple((group, key, type(value).__name__) for group in ('properties', 'measurements')
                                   for key, value in base.get(group, {}).items()
                                   if type(value) in (int, float, str, bool))
                    if fields:
                        meanings.setdefault(event, set()).add(fields)
                except (KeyError, ValueError, TypeError, AttributeError):
                    continue
    return {event: next(iter(shapes)) for event, shapes in meanings.items() if len(shapes) == 1}


def recipe(key, tid, stat, block, field_index, known):
    event, *parts = key.split('.')
    if len(parts) % 2 or not event:
        raise ValueError(f'Malformed stat key: {key}')
    fields = dict(zip(parts[::2], parts[1::2], strict=True))
    if len(fields) != len(parts)//2:
        raise ValueError(f'Duplicate fields: {key}')
    shape = known.get(event, ())
    if fields:
        groups = {key: (group, kind) for group, key, kind in shape}
        fields = {k: int(v) if re.fullmatch(r'-?(?:0|[1-9][0-9]*)', v) else v for k, v in fields.items()}
        field = list(fields)[field_index]
        default = fields[field]
        kind = 'Integer' if type(default) is int else 'String'
        fields[field] = SLOT
        properties = {k: v for k, v in fields.items() if groups.get(k, ('properties', None))[0] == 'properties'}
        measurements = {k: v for k, v in fields.items() if groups.get(k, ('properties', None))[0] == 'measurements'}
    else:
        field, default, kind = 'Value', '', stat['Type']
        if len(shape) == 1:
            group, field, value_type = shape[0]
            kind = {'int': 'Integer', 'float': 'Double', 'str': 'String', 'bool': 'Boolean'}[value_type]
        else:
            group = 'properties' if kind == 'String' else 'measurements'
        properties, measurements = ({field: SLOT}, {}) if group == 'properties' else ({}, {field: SLOT})
    scid = stat.get('ServiceConfigId') or block['ServiceConfigId']
    data = {'baseType': 'Microsoft.XboxLive.InGame', 'baseData': {'name': event, 'serviceConfigId': scid,
            'playerSessionId': 'REPLACESESSIONGUID', 'titleId': tid, 'userId': 'REPLACEXUID', 'ver': 1,
            'properties': properties, 'measurements': measurements}}
    return {'Type': kind, 'PayloadField': field, 'SuggestedValue': default,
            'DisplayName': f"{stat.get('DisplayName', key)} [{field}]",
            'Request1': {'EventName': op('REPLACEEVENTNAME', json.dumps(event, ensure_ascii=False)[1:-1]),
                         'Metadata': op('REPLACEMETADATA', '{"policies":0}'),
                         'Data': op('REPLACEDATA', json.dumps(data, ensure_ascii=False, separators=(',', ':')))}}


def main():
    p = argparse.ArgumentParser()
    p.add_argument('source', type=Path)
    p.add_argument('roots', type=Path, nargs='+')
    a = p.parse_args()
    source = {}
    for d in sorted(a.source.iterdir()):
        f = d / 'Stats.json'
        if f.is_file():
            tid = d.name.split(' - ', 1)[0]
            source[tid] = (d, parse(f.read_bytes())[tid])
    for root in a.roots:
        aggregate = parse((root / 'Stats.json').read_bytes())
        generated = 0
        empty = []
        for tid, (folder, original) in source.items():
            path = root / tid / 'Stats.json'
            wrapper = parse(path.read_bytes())
            block = wrapper[tid]
            stats = block['Stats']
            if not stats:
                empty.append(tid)
            candidates = [(k, v) for k, v in original['Stats'].items() if isinstance(v, dict) and 'Source' in v and 'Type' in v and not any(re.fullmatch(r'Request\d+', key) for key in v)]
            if not candidates:
                continue
            template = root / tid / f'{tid}.json'
            if not template.exists():
                template = folder / f'{tid}.json'
            raw = template.read_text(encoding='utf-8-sig')
            data_file = template.with_name('Data.json')
            if not data_file.exists():
                data_file = folder / 'Data.json'
            known = known_payloads(raw, data_file, tid)
            try:
                block['StatTemplate'] = envelope(raw)
            except (ValueError, TypeError):
                data_file = template.with_name('Data.json')
                if not data_file.exists():
                    data_file = folder / 'Data.json'
                try:
                    block['StatTemplate'] = rendered_envelope(raw, data_file, tid)
                except (ValueError, TypeError, KeyError, OSError) as e:
                    print(tid, template, e)
                    continue
            for key, stat in candidates:
                if stats[key].get('Request1') and not stats[key].get('PayloadField'):
                    continue
                field_count = max(1, (len(key.split('.')) - 1)//2)
                for i in range(field_count):
                    row = recipe(key, tid, stat, block, i, known)
                    label = key if i == 0 else key + ' [' + row['PayloadField'] + ']'
                    if label in stats and i and not stats[label].get('PayloadField'):
                        raise ValueError(f'Conflicting stat {tid}/{label}')
                    stats[label] = row
                    generated += 1
            dump(path, wrapper)
            aggregate[tid] = block
        dump(root / 'Stats.json', aggregate)
        print(f'{root}: {len(source)} source titles; {generated} generated rows; {len(empty)} empty titles')


if __name__ == '__main__':
    main()
