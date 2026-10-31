import hashlib,io,json,unittest,zipfile
from publish_events import build,parse,select_release,event_asset
from export_event_pack import export
class FeedChecks(unittest.TestCase):
    def test_mixed_platform_releases(self):
        events={'tag_name':'v1.0.8','draft':False,'prerelease':False,'assets':[{'name':'Events.zip'}]}
        mobile={'tag_name':'ios-v0.12.5','draft':False,'prerelease':False,'assets':[{'name':'App.ipa'}]}
        draft=dict(events,draft=True);beta=dict(events,prerelease=True)
        self.assertEqual(select_release([mobile,draft,beta,events]),events)
        with self.assertRaises(ValueError): select_release([mobile,draft,beta])
    def test_indexed_asset_preferred(self):
        release={'tag_name':'v1.0.9','draft':False,'prerelease':False,'assets':[{'name':'Events.zip'},{'name':'Events-Indexed.zip'}]}
        self.assertEqual(select_release([release]),release)
        self.assertEqual(event_asset(release)['name'],'Events-Indexed.zip')
        release['assets'].append({'name':'Events-Indexed.zip'})
        with self.assertRaises(ValueError): event_asset(release)
    def archive(self,data,extra=None):
        buffer=io.BytesIO()
        with zipfile.ZipFile(buffer,'w') as z:
            z.writestr('Events/Data.json',json.dumps(data));z.writestr('Events/7.json','{"data":{"baseData":{"titleId":"7","serviceConfigId":"00000000-0000-0000-0000-000000000001","id":REPLACEID}}}')
            if extra: z.writestr(extra,'bad')
        blob=buffer.getvalue();release={'tag_name':'v1.0.8','assets':[{'name':'Events.zip','digest':'sha256:'+hashlib.sha256(blob).hexdigest()}]}
        return release,blob
    def data(self): return {'7':{'Achievements':{'1':{'0':{'ReplacementType':'Replace','Target':'REPLACEID','Replacement':'1'}}}},'SupportedTitleIDs':['7']}
    def test_valid_and_reserved(self):
        r,b=self.archive(self.data());self.assertEqual(len(build(r,b,set())[0]['titles']),1);self.assertFalse(build(r,b,{'7'})[0]['titles'])
    def test_folder_archive(self):
        data=self.data();r,flat=self.archive(data);buffer=io.BytesIO()
        with zipfile.ZipFile(io.BytesIO(flat)) as source,zipfile.ZipFile(buffer,'w') as target:
            target.writestr('Events/index.json',json.dumps({'7':{},'SupportedTitleIDs':['7']}))
            target.writestr('Events/7/Data.json',json.dumps(data))
            target.writestr('Events/7/7.json',source.read('Events/7.json'))
        archive=buffer.getvalue();r['assets'][0]['digest']='sha256:'+hashlib.sha256(archive).hexdigest()
        self.assertEqual(build(r,archive,set())[0]['titles'],build(self.archive(data)[0],flat,set())[0]['titles'])
        self.assertFalse(build(r,archive,{'7'})[0]['titles'])
    def test_digest_and_paths(self):
        r,b=self.archive(self.data());r['assets'][0]['digest']='sha256:'+'0'*64
        with self.assertRaises(ValueError): build(r,b,set())
    def test_offline_pack(self):
        _,archive=self.archive(self.data());encoded,count,skipped=export(archive)
        self.assertEqual(count,0);self.assertEqual(skipped,1)
        pack=parse(encoded);self.assertEqual(pack['schemaVersion'],1)
        buffer=io.BytesIO()
        with zipfile.ZipFile(io.BytesIO(archive)) as source,zipfile.ZipFile(buffer,'w') as target:
            for name in source.namelist():
                body=source.read(name)
                if name=='Events/7.json': body=body.replace(b'{"data":',b'{"name":"Microsoft.XboxLive.T7.Test","data":').replace(b'"id":REPLACEID',b'"name":"Test","id":REPLACEID')
                target.writestr(name,body)
        encoded,count,skipped=export(buffer.getvalue());self.assertEqual(count,1)
        self.assertEqual(parse(encoded)['titles']['7']['recipes']['recipes']['1'][0][0]['target'],'REPLACEID')
        r,b=self.archive(self.data(),'Events/../bad')
        with self.assertRaises(ValueError): build(r,b,set())
    def test_order_duplicates_and_unsupported(self):
        d=self.data();d['9']={};r,b=self.archive(d)
        with self.assertRaises(ValueError): build(r,b,set())
        with self.assertRaises(ValueError): parse(b'{"a":1,"a":2}')
        d=self.data();d['7']['Achievements']['1']['0']['ReplacementType']='RawBody';r,b=self.archive(d)
        self.assertFalse(build(r,b,set())[0]['titles'])
if __name__=='__main__': unittest.main()
