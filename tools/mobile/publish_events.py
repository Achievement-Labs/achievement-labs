"""Publish public validated additions from the original repo's Events.zip. No account data."""
import hashlib,io,json,os,re,urllib.request,urllib.error,zipfile
from datetime import datetime,timezone
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
def read(url,limit,token=None,method='GET',body=None):
    headers={'User-Agent':'AchievementLabs-Mobile-Catalogue/1','Accept':'application/vnd.github+json'}
    if token: headers['Authorization']='Bearer '+token
    req=urllib.request.Request(url,data=None if body is None else json.dumps(body).encode(),headers=headers,method=method)
    with urllib.request.urlopen(req,timeout=90) as r: data=r.read(limit+1)
    if len(data)>limit: raise ValueError('Response limit')
    return data

def unique(pairs):
    result={}
    for key,value in pairs:
        if key in result: raise ValueError('Duplicate JSON property')
        result[key]=value
    return result

def parse(data): return json.loads(data.decode('utf-8-sig'),object_pairs_hook=unique)
def normalize(tid,block,template):
    if len(template.encode())>1024*1024: raise ValueError('Template limit')
    result={};achievements=block.get('Achievements',{})
    if not isinstance(achievements,dict) or len(achievements)>10000: raise ValueError('Achievement limit')
    for aid,entry in achievements.items():
        if not re.fullmatch(r'[0-9]{1,10}',aid): continue
        try:
            first=next(iter(entry.values())); requests=[entry] if 'ReplacementType' in first else list(entry.values())
            if not 0<len(requests)<=100: raise ValueError('Request limit')
            normalized=[]
            for request in requests:
                body=template;operations=[]
                if not 0<len(request)<=100: raise ValueError('Operation limit')
                for op in request.values():
                    target,value=op['Target'],op['Replacement']
                    if op['ReplacementType']!='Replace' or not isinstance(target,str) or not target or target not in body or not isinstance(value,str): raise ValueError('Unsupported replacement')
                    if len(body)+body.count(target)*(len(value)-len(target))>2*1024*1024: raise ValueError('Expansion limit')
                    body=body.replace(target,value);operations.append({'target':target,'value':value})
                for target,value in {'REPLACESEQ':'1','REPLACETIME':'2026-10-17T00:00:00.000Z','REPLACEXUID':'0','REPLACESESSIONGUID':'00000000-0000-0000-0000-000000000001'}.items(): body=body.replace(target,value)
                if re.search(r'REPLACE[A-Z0-9_]+',body): raise ValueError('Unresolved placeholder')
                base=parse(body.encode())['data']['baseData']
                if str(base['titleId'])!=tid or not re.fullmatch(r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}',base['serviceConfigId']): raise ValueError('Wrong identity')
                normalized.append(operations)
            result[aid]=normalized
        except (KeyError,TypeError,ValueError,StopIteration): continue
    if not result: raise ValueError('No supported recipes')
    return {'template':template,'recipes':{'recipes':result}}

def build(release,archive,reserved):
    tag=release['tag_name']
    if release.get('draft') or release.get('prerelease') or not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+(?:[-.][A-Za-z0-9.-]+)?',tag): raise ValueError('Unexpected release')
    asset=next(a for a in release['assets'] if a['name']=='Events.zip');digest=asset.get('digest','')
    if not re.fullmatch(r'sha256:[a-fA-F0-9]{64}',digest) or hashlib.sha256(archive).hexdigest()!=digest[7:].lower(): raise ValueError('Archive checksum mismatch')
    titles={};skipped=0
    with zipfile.ZipFile(io.BytesIO(archive)) as z:
        if len(z.infolist())>2500 or sum(i.file_size for i in z.infolist())>256*1024*1024: raise ValueError('Archive limit')
        entries={}
        for info in z.infolist():
            name=info.filename.replace('\\','/')
            if name.startswith('/') or any(p in ('.','..') for p in name.split('/')) or not name.startswith('Events/'): raise ValueError('Unexpected path')
            if info.is_dir(): continue
            short=name[7:]
            if '/' in short or short.lower() in entries: raise ValueError('Ambiguous archive')
            entries[short.lower()]=info
        info=entries['data.json']
        if info.file_size>128*1024*1024: raise ValueError('Catalogue limit')
        data=parse(z.read(info))
        if list(data)[-1]!='SupportedTitleIDs': raise ValueError('SupportedTitleIDs must remain last')
        ids=data['SupportedTitleIDs']
        if len(ids)>5000: raise ValueError('Title limit')
        for tid in dict.fromkeys(map(str,ids)):
            if tid in reserved: continue
            try:
                if not re.fullmatch(r'[0-9]{1,10}',tid) or data[tid].get('Enabled') is False: raise ValueError('Disabled/invalid')
                info=entries[tid+'.json']
                if info.file_size>1024*1024: raise ValueError('Template limit')
                titles[tid]=normalize(tid,data[tid],z.read(info).decode('utf-8-sig'))
            except (KeyError,TypeError,ValueError,UnicodeError): skipped+=1
    return {'schemaVersion':1,'upstreamRelease':tag,'archiveSHA256':digest[7:].lower(),'generatedAt':datetime.now(timezone.utc).isoformat().replace('+00:00','Z'),'titles':titles},skipped

def publish(feed,pack=None):
    repo=os.environ['GITHUB_REPOSITORY'];token=os.environ['GH_TOKEN'];api='https://api.github.com/repos/'+repo
    def call(path,method='GET',body=None): return parse(read(api+path,40*1024*1024,token,method,body))
    parent=None
    try: parent=call('/git/ref/heads/mobile-event-feed')['object']['sha']
    except urllib.error.HTTPError as e:
        if e.code!=404: raise
    entries=[{'path':'catalogue.json','mode':'100644','type':'blob','content':json.dumps(feed,separators=(',',':'))}]
    if pack is not None: entries.append({'path':'EventPack.json','mode':'100644','type':'blob','content':pack.decode('utf-8')})
    tree=call('/git/trees','POST',{'tree':entries})
    commit=call('/git/commits','POST',{'message':'Refresh public mobile additions from '+feed['upstreamRelease'],'tree':tree['sha'],'parents':[] if parent is None else [parent]})
    if parent: call('/git/refs/heads/mobile-event-feed','PATCH',{'sha':commit['sha']})
    else: call('/git/refs','POST',{'ref':'refs/heads/mobile-event-feed','sha':commit['sha']})

def main():
    release=parse(read('https://api.github.com/repos/Achievement-Labs/achievement-labs/releases/latest',1024*1024));tag=release['tag_name']
    asset=next(a for a in release['assets'] if a['name']=='Events.zip')
    expected='https://github.com/Achievement-Labs/achievement-labs/releases/download/'+tag+'/Events.zip'
    if asset['browser_download_url']!=expected: raise ValueError('Unexpected archive location')
    archive=read(expected,32*1024*1024);data=parse((ROOT/'src/AchievementLabs.App/Events/Data.json').read_bytes())
    if list(data)[-1]!='SupportedTitleIDs': raise ValueError('Bundled SupportedTitleIDs must be last')
    feed,skipped=build(release,archive,set(data));encoded=json.dumps(feed,separators=(',',':')).encode()
    if len(encoded)>32*1024*1024: raise ValueError('Feed limit')
    from export_event_pack import export
    pack,pack_titles,_=export(archive,tag)
    publish(feed,pack);print(json.dumps({'release':feed['upstreamRelease'],'newMobileTitles':len(feed['titles']),'packTitles':pack_titles,'skipped':skipped,'bytes':len(encoded)}))
if __name__=='__main__': main()
