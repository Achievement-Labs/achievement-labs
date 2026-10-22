import hashlib,io,json,unittest,zipfile
from publish_events import build,parse
class FeedChecks(unittest.TestCase):
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
    def test_digest_and_paths(self):
        r,b=self.archive(self.data());r['assets'][0]['digest']='sha256:'+'0'*64
        with self.assertRaises(ValueError): build(r,b,set())
        r,b=self.archive(self.data(),'Events/../bad')
        with self.assertRaises(ValueError): build(r,b,set())
    def test_order_duplicates_and_unsupported(self):
        d=self.data();d['9']={};r,b=self.archive(d)
        with self.assertRaises(ValueError): build(r,b,set())
        with self.assertRaises(ValueError): parse(b'{"a":1,"a":2}')
        d=self.data();d['7']['Achievements']['1']['0']['ReplacementType']='RawBody';r,b=self.archive(d)
        self.assertFalse(build(r,b,set())[0]['titles'])
if __name__=='__main__': unittest.main()
