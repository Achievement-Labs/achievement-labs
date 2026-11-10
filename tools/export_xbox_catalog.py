"""Export a small, read-only runtime metadata index from the author's XboxTitles.db."""
import argparse
from contextlib import closing
import gzip
import hashlib
import json
from pathlib import Path
import re
import sqlite3


def legacy_ids(repo):
    text=(Path(repo)/'src/AchievementLabs.Enhancements/GfwlTitles.cs').read_text(encoding='utf-8')
    result=[]
    for field in ('Exclusive','Shared'):
        match=re.search(r'HashSet<string> '+field+r'.*?\{([^}]+)\}',text,re.S)
        if not match: raise ValueError('GFWL identity source changed; update the exporter.')
        result.append(set(re.findall(r'"([0-9]+)"',match[1])))
    return result


def devices(value):
    return [p.strip() for p in (value or '').split(',') if p.strip() and p.strip()!='N/A']


def normalize(key,raw,version,exclusive,shared):
    values=list(dict.fromkeys(raw))
    if key in exclusive or version==1 and 'Xbox360' in values and 0x0FA0 <= int(key)&0xffff <= 0x0FFF and key not in shared:
        return ['GFWL']
    if key in shared:
        return list(dict.fromkeys(['Xbox360','GFWL',*values]))
    if version==1 and 'PC' in values:
        values=['Windows8' if p=='PC' else p for p in values]
    aliases={'windows 8':'Windows8','windows8':'Windows8','win8':'Windows8','ios':'iOS','android':'Android','web':'Web',
             'windowsphone':'WindowsPhone','windows phone':'WindowsPhone','gfwl':'GFWL'}
    return list(dict.fromkeys(aliases.get(p.casefold(),p) for p in values)) or ['Unknown']


def export(database,repo):
    path=Path(database).resolve()
    with closing(sqlite3.connect(path.as_uri()+'?mode=ro',uri=True)) as db:
        db.row_factory=sqlite3.Row
        db.execute('PRAGMA query_only=ON');db.execute('BEGIN')
        catalog={r['TitleId']:dict(r) for r in db.execute('SELECT * FROM CatalogTitle')}
        filtered={r['TitleId']:dict(r) for r in db.execute('SELECT * FROM FilteredTitle')}
        exclusive,shared=legacy_ids(repo)
        rows=[]
        for key in sorted(catalog.keys()|filtered.keys(),key=int):
            if not key.isascii() or not key.isdigit() or not 0<int(key)<=4294967295 or str(int(key))!=key:
                raise ValueError('Invalid canonical title ID.')
            base=catalog.get(key,{});detail=filtered.get(key,{})
            raw=list(dict.fromkeys(devices(base.get('Devices'))+devices(detail.get('Devices'))))
            version=detail.get('AchievementSourceVersion')
            if version is None:version=base.get('AchievementSourceVersion')
            achievement=detail.get('AchievementsType')
            stats=detail.get('StatsType')
            if achievement not in (None,'Title Based','Event Based'):raise ValueError('Unknown achievement type.')
            if stats not in (None,'Title Based','Event Based'):raise ValueError('Unknown stat type.')
            # FilteredTitle.AchievementCount can count rule-covered subsets, not all definitions.
            total=base.get('TotalAchievements') or None
            if total is not None and (type(total) is not int or not 1<=total<=10000):raise ValueError('Invalid definition count.')
            name=detail.get('Name') or base.get('Name') or key
            if not isinstance(name,str) or not name.strip() or len(name)>1024:raise ValueError('Invalid title name.')
            rows.append(dict(TitleId=key,Name=name,Devices=raw,Platforms=normalize(key,raw,version,exclusive,shared),
                SourceVersion=version,AchievementsType=achievement,StatsType=stats,TotalAchievements=total,
                ModernTitleId=base.get('ModernTitleId') or None))
    encoded=json.dumps(rows,ensure_ascii=False,separators=(',',':')).encode('utf-8')
    return dict(SchemaVersion=1,Revision=hashlib.sha256(encoded).hexdigest(),Titles=rows)


def load_snapshot(path):
    value=json.loads(gzip.decompress(Path(path).read_bytes()))
    if value['SchemaVersion']!=1 or not isinstance(value['Titles'],list):raise ValueError('Invalid runtime catalog.')
    return value


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--database',type=Path);p.add_argument('--repo',type=Path,default=Path('.'))
    p.add_argument('--output',type=Path,default=Path('catalog/xbox-title-catalog.json.gz'));p.add_argument('--check',action='store_true');a=p.parse_args()
    if not a.database:p.error('--database is required')
    value=export(a.database,a.repo)
    packed=gzip.compress(json.dumps(value,ensure_ascii=False,separators=(',',':')).encode('utf-8'),compresslevel=9,mtime=0)
    if a.check:
        if load_snapshot(a.output)!=value:raise ValueError('Runtime snapshot is stale.')
    else:
        a.output.parent.mkdir(parents=True,exist_ok=True)
        temporary=a.output.with_name(a.output.name+'.tmp');temporary.write_bytes(packed);temporary.replace(a.output)
    print(json.dumps(dict(titles=len(value['Titles']),bytes=len(packed),revision=value['Revision'])))


if __name__=='__main__':main()
