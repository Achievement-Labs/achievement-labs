"""Export public metadata for offline desktop search from canonical runtime sources."""
import argparse,json,subprocess
from pathlib import Path
from .build import project,digest

def export(repo):
    titles,totals=project(repo)
    source=subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip()
    return dict(catalogRevision=digest({'titles':titles,'totals':totals}),sourceRevision=source,
                totalMatched=len(titles),items=sorted(titles,key=lambda t:(t['name'].casefold(),t['titleId'])),nextCursor=None)

def main():
    p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,default=Path('.'));p.add_argument('--check',action='store_true');a=p.parse_args()
    value=export(a.repo);path=a.repo/'catalog/public-metadata.json'
    if a.check:
        old=json.loads(path.read_text(encoding='utf-8'))
        # The stored revision identifies the unchanged canonical inputs, not later UI-only commits.
        assert old['catalogRevision']==value['catalogRevision'] and sorted(old['items'],key=lambda t:t['titleId'])==sorted(value['items'],key=lambda t:t['titleId']), 'Regenerate bundled public metadata'
    else:path.write_text(json.dumps(value,ensure_ascii=False,separators=(',',':')),encoding='utf-8')
    print(json.dumps({'bundledTitles':len(value['items']),'catalogRevision':value['catalogRevision']}))
if __name__=='__main__':main()
