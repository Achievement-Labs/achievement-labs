"""Build mobile resources from committed desktop data; never rewrite Events/Data.json.

Preserves ordinary standalone templates and operation order. Unsupported operations,
unresolved placeholders and edition routes without a mobile guard are omitted explicitly.
"""
import collections
import json
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'src/AchievementLabs.App/Events'
OUTPUT = ROOT / 'mobile/Catalogue'

# Frozen semantic signatures from the desktop's reviewed public achievement fixture.
# This permits ordinary replacements only; live feeds cannot overwrite bundled IDs.
EDITIONS = json.loads((Path(__file__).with_name('edition_mappings.json')).read_text(encoding='utf-8'))

def edition_routes(tid, block):
    edition = EDITIONS.get(tid)
    if not edition: return None
    entries = edition['Entries']
    actual = set(block.get('Achievements', {}))
    legacy = {entry['CatalogId'] for entry in entries}
    corrected = {entry['XboxId'] for entry in entries}
    assert len(legacy) == len(corrected) == len(entries), 'Duplicate reviewed edition IDs'
    assert actual == legacy or actual == corrected, 'Edition catalogue IDs changed; mapping needs review.'
    return {entry['CatalogId'] if actual == legacy else entry['XboxId']: entry for entry in entries}

def signature(text):
    event = json.loads(text); base = event['data']['baseData']
    return {'Event':event['name'], 'BaseName':base.get('name'), 'TitleId':str(base['titleId']),
            'Scid':base['serviceConfigId'], 'Properties':base.get('properties'), 'Measurements':base.get('measurements')}


def prepare():
    data = json.loads((SOURCE / 'Data.json').read_text(encoding='utf-8-sig'))
    assert list(data)[-1] == 'SupportedTitleIDs', 'SupportedTitleIDs must be last'
    assert (ROOT / 'Events/Data.json').read_bytes() == (SOURCE / 'Data.json').read_bytes(), 'Catalogue copies differ'
    if OUTPUT.exists(): shutil.rmtree(OUTPUT)
    (OUTPUT / 'Events').mkdir(parents=True)
    guarded = {g['TitleId'] for g in json.loads((ROOT / 'src/AchievementLabs.Enhancements/EditionMappings.json').read_text())}
    # Preserve the released mobile edition guards even when desktop guard metadata differs.
    guarded.update({'345903534', '796798669', '125965001', '2154930'})
    guarded.difference_update(EDITIONS)
    supported = {str(x) for x in data['SupportedTitleIDs']}
    index, failures = {}, collections.Counter()
    for tid, block in data.items():
        if not tid.isdecimal(): continue
        reason = None
        if tid in guarded: reason = 'This edition needs its desktop routing/mapping guard ported before mobile event actions can be enabled.'
        elif block.get('Enabled') is False or tid not in supported: reason = 'Event actions are disabled in the source catalogue.'
        path = SOURCE / f'{tid}.json'
        if not path.exists(): reason = reason or 'No standalone event template.'
        recipes, routes = {}, None
        if not reason:
            try: routes = edition_routes(tid, block)
            except AssertionError as e: reason = str(e)
        if not reason:
            template = path.read_text(encoding='utf-8-sig')
            # Current bundled Ghosts already has corrected Xbox IDs; reject any shifted set.
            if tid == '572802557':
                assert set(block['Achievements']) == {str(i) for i in list(range(1,51))+list(range(52,93))}
            for aid, entry in block.get('Achievements', {}).items():
                try:
                    first = next(iter(entry.values()))
                    requests = [entry] if 'ReplacementType' in first else list(entry.values())
                    assert 0 < len(requests) <= 1000
                    normalized, signatures = [], []
                    for request in requests:
                        body, operations = template, []
                        for op in request.values():
                            assert op['ReplacementType'] == 'Replace', 'non-deterministic/unsupported operation'
                            target, value = op['Target'], op.get('Replacement', '')
                            assert isinstance(target, str) and target and target in body, 'missing replacement target'
                            if not isinstance(value, str): value = str(value) if not isinstance(value, (dict,list)) else json.dumps(value,separators=(',',':'))
                            body = body.replace(target,value)
                            operations.append({'target':target,'value':value})
                        assert operations
                        for k,v in {'REPLACESEQ':'1','REPLACETIME':'2026-10-17T00:00:00.0000000Z','REPLACEXUID':'0','REPLACESESSIONGUID':'00000000-0000-0000-0000-000000000001'}.items(): body=body.replace(k,v)
                        assert not re.search(r'REPLACE[A-Z0-9_]+',body), 'unresolved placeholder'
                        parsed=json.loads(body)['data']['baseData']
                        assert str(parsed['titleId']) == tid, 'wrong title identity'
                        assert re.fullmatch(r'[0-9a-fA-F-]{36}',parsed['serviceConfigId']), 'missing service identity'
                        normalized.append(operations)
                        if routes is not None: signatures.append(signature(body))
                    if routes is not None:
                        assert signatures == routes[aid]['Expected'], 'Edition event differs from reviewed mapping.'
                    recipes[routes[aid]['XboxId'] if routes is not None else aid]=normalized
                except (AssertionError,KeyError,TypeError,ValueError,StopIteration) as e: failures[str(e) or type(e).__name__]+=1
            if routes is not None and len(recipes) != len(routes):
                recipes = {}
                reason = 'Edition event data changed; the reviewed mobile mapping needs an update.'
            if recipes:
                shutil.copyfile(path,OUTPUT/'Events'/f'{tid}.json')
                (OUTPUT/'Events'/f'{tid}.mapping.json').write_text(json.dumps({'recipes':recipes},separators=(',',':')))
            else: reason=reason or 'No deterministic recipes passed the mobile payload checks.'
        index[tid]={'count':len(recipes),'classification':'Supported' if block.get('FullySupported') is True else 'Partially supported' if block.get('PartiallySupported') is True else 'Testing','reason':reason}
    (OUTPUT/'MobileIndex.json').write_text(json.dumps(index,separators=(',',':')))
    for name in ['achievement-packs.json','achievement-hubs.json','achievement-totals.json']: shutil.copyfile(ROOT/'catalog'/name,OUTPUT/name)
    summary={'sourceTitleBlocks':len(index),'mobileTitles':sum(v['count']>0 for v in index.values()),'recipes':sum(v['count'] for v in index.values()),'omittedRecipes':dict(failures),'guardedEditions':sorted(guarded)}
    (OUTPUT/'catalogue-report.json').write_text(json.dumps(summary,indent=2)+'\n')
    print(json.dumps(summary,indent=2))

if __name__=='__main__': prepare()
