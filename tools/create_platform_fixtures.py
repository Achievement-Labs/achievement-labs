"""Create fictional, account-free inputs for --platform-ui-check in a new directory."""
import argparse
import json
import shutil
import sqlite3
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("output", type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
database = args.output / "galaxy-fixture.db"
with sqlite3.connect(database) as db:
    db.executescript("""
    CREATE TABLE Products(id INTEGER PRIMARY KEY,name TEXT);
    CREATE TABLE InstalledBaseProducts(productId INTEGER,installationPath TEXT);
    CREATE TABLE GamePieceTypes(id INTEGER,type TEXT);
    CREATE TABLE GamePieces(releaseKey TEXT,gamePieceTypeId INTEGER,value TEXT,userId INTEGER);
    CREATE TABLE Achievements(gameReleaseKey TEXT,apikey TEXT,imageUnlockedUrl TEXT,imageLockedUrl TEXT,rarity REAL);
    CREATE TABLE LocalizedAchievements(gameReleaseKey TEXT,apikey TEXT,name TEXT,description TEXT,languageId INTEGER);
    CREATE TABLE UserAchievements(gameReleaseKey TEXT,userId INTEGER,apikey TEXT,unlockTime TEXT,isUnlocked INTEGER);
    INSERT INTO Products VALUES(123,'Galaxy fixture game');
    INSERT INTO GamePieceTypes VALUES(1,'title');
    INSERT INTO GamePieces VALUES('gog_123',1,'Galaxy fixture game',NULL);
    INSERT INTO Achievements VALUES('gog_123','FIRST','','',72.5),('gog_123','RARE','','',2.5),('gog_123','UNKNOWN','','',NULL);
    INSERT INTO LocalizedAchievements VALUES('gog_123','FIRST','First steps','Complete the introduction.',1),('gog_123','RARE','Hidden chamber','Find the hidden room.',1),('gog_123','UNKNOWN','Final challenge','Finish the story.',1);
    INSERT INTO UserAchievements VALUES('gog_123',101,'FIRST','2026-10-20T12:00:00Z',1),('gog_123',101,'RARE',NULL,0),('gog_123',202,'FIRST',NULL,0),('gog_123',202,'RARE','2026-10-21T12:00:00Z',1);
    """)
db.close()
# Keep an uncheckpointed database + WAL pair to exercise temporary-copy reading.
with sqlite3.connect(database) as db:
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("INSERT INTO Products VALUES(456,'Uncheckpointed WAL fixture')")
    db.commit()
    shutil.copyfile(database, args.output / "galaxy-wal-fixture.db")
    shutil.copyfile(str(database) + "-wal", args.output / "galaxy-wal-fixture.db-wal")
db.close()
for platform in ("EA", "GOG", "Epic", "Ubisoft"):
    snapshot = dict(schemaVersion=1, platform=platform, gameId="fixture-" + platform,
                    title=platform + " fixture game", accountId="offline-fixture", catalogComplete=True,
                    achievements=[
                        dict(id="FIRST", name="First steps", description="Complete the introduction.", unlocked=True, globalPercent=72.5),
                        dict(id="RARE", name="Hidden chamber", description="Find the hidden room.", unlocked=False, globalPercent=2.5),
                        dict(id="UNKNOWN", name="Final challenge", description="Finish the story.", unlocked=False),
                    ])
    (args.output / (platform + ".json")).write_text(json.dumps(snapshot, indent=2), encoding="utf-8")
