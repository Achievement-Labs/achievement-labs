"""Convert a saved Events.zip to the offline event pack accepted by all apps."""
import argparse, hashlib, json
from pathlib import Path
from publish_events import build

def export(archive,tag='v0.0.0-local'):
    release = {'tag_name':tag, 'assets':[{'name':'Events.zip','digest':'sha256:'+hashlib.sha256(archive).hexdigest()}]}
    pack, skipped = build(release, archive, set())
    # Use the same conservative request/operation limits on all three platforms.
    for title in list(pack['titles']):
        value = pack['titles'][title]
        recipes = value['recipes']['recipes']
        for achievement in list(recipes):
            requests = recipes[achievement]
            compatible = len(requests)<=20 and sum(map(len,requests))<=100
            for operations in requests:
                body=value['template']
                for op in operations: body=body.replace(op['target'],op['value'])
                for target,replacement in {'REPLACESEQ':'1','REPLACETIME':'2026-01-01T00:00:00Z','REPLACEXUID':'0','REPLACESESSIONGUID':'00000000-0000-0000-0000-000000000001'}.items(): body=body.replace(target,replacement)
                payload=json.loads(body);base=payload.get('data',{}).get('baseData',{})
                compatible = compatible and payload.get('name','').startswith('Microsoft.XboxLive.T'+title+'.') and bool(base.get('name'))
            if not compatible:
                del recipes[achievement]
        if not recipes:
            del pack['titles'][title]; skipped += 1
    encoded = json.dumps(pack,separators=(',',':'),ensure_ascii=False).encode()
    if len(encoded)>32*1024*1024: raise ValueError('Event pack exceeds 32 MB')
    return encoded, len(pack['titles']), skipped

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive',type=Path); parser.add_argument('output',type=Path)
    args=parser.parse_args()
    encoded,count,skipped=export(args.archive.read_bytes())
    args.output.write_bytes(encoded)
    print(f'Wrote {count} titles; skipped {skipped} unsupported entries. No account data included.')
