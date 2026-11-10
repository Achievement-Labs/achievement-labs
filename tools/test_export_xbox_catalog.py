import hashlib
from contextlib import closing
import sqlite3
import tempfile
import unittest
from pathlib import Path
from tools.export_xbox_catalog import export,normalize

class XboxCatalogExportTests(unittest.TestCase):
    def test_platforms_and_generations(self):
        exclusive={'1297287434'};shared={'1297287126'}
        self.assertEqual(normalize('1297287434',['Xbox360'],1,exclusive,shared),['GFWL'])
        self.assertIn('Xbox360',normalize('1297287126',['Xbox360'],1,exclusive,shared))
        self.assertIn('GFWL',normalize('1297287126',['Xbox360'],1,exclusive,shared))
        self.assertEqual(normalize('7',['PC'],1,exclusive,shared),['Windows8'])
        self.assertEqual(normalize('7',['PC'],2,exclusive,shared),['PC'])
        for platform in ['iOS','Android','Web','Kindle Fire','Gear VR','Apple TV','WindowsPhone']:
            self.assertEqual(normalize('7',[platform],2,exclusive,shared),[platform])
        self.assertEqual(normalize('7',['Mobile'],1,exclusive,shared),['Mobile'])
        self.assertEqual(normalize('7',[],None,exclusive,shared),['Unknown'])

    def test_readonly_union_counts_types_and_projection(self):
        with tempfile.TemporaryDirectory() as root:
            path=Path(root)/'author.db'
            with closing(sqlite3.connect(path)) as db, db:
                db.executescript('''CREATE TABLE CatalogTitle(TitleId TEXT PRIMARY KEY,Name TEXT,Devices TEXT,AchievementSourceVersion INTEGER,TotalAchievements INTEGER,ModernTitleId TEXT,Secret TEXT);
                CREATE TABLE FilteredTitle(TitleId TEXT PRIMARY KEY,Name TEXT,Devices TEXT,AchievementSourceVersion INTEGER,AchievementCount INTEGER,AchievementsType TEXT,StatsType TEXT);''')
                db.execute('INSERT INTO CatalogTitle VALUES(?,?,?,?,?,?,?)',('7','Legacy','PC',1,20,'8','NEVER-EXPORT-THIS'))
                db.execute('INSERT INTO FilteredTitle VALUES(?,?,?,?,?,?,?)',('7',None,'PC',1,5,None,None))
                db.execute('INSERT INTO FilteredTitle VALUES(?,?,?,?,?,?,?)',('9','Mobile','iOS',2,12,'Event Based','Title Based'))
            before=hashlib.sha256(path.read_bytes()).hexdigest()
            value=export(path,Path(__file__).resolve().parents[1]);again=export(path,Path(__file__).resolve().parents[1])
            self.assertEqual(before,hashlib.sha256(path.read_bytes()).hexdigest())
            self.assertEqual(value,again)
            self.assertEqual(len(value['Titles']),2)
            self.assertEqual(value['Titles'][0]['Platforms'],['Windows8'])
            self.assertEqual(value['Titles'][1]['StatsType'],'Title Based')
            self.assertEqual(value['Titles'][0]['TotalAchievements'],20)
            self.assertIsNone(value['Titles'][1]['TotalAchievements'])
            self.assertNotIn('NEVER-EXPORT-THIS',str(value))
            self.assertIsNone(value['Titles'][0]['AchievementsType'])

if __name__=='__main__':unittest.main()
