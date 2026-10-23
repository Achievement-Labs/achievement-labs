import XCTest
import CryptoKit
@testable import LabsMobileCore
final class PublicSyncTests: XCTestCase {
    func testEventOnlyTransferOmitsAuthorizationAndRejectsInvalidKinds() throws {
        let key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"
        let valid = try PCTransfer.open(Data(#"{"schemaVersion":1,"nonce":"AAECAwQFBgcICQoL","ciphertext":"PCCleK2Ar3rbJOX42IYWT7n2thjQWTQVVgPHvz1LZcRkftqowKp39laIX8/w8kFczGNAr27kgfYftUtsbIuanJlGpw66vkhDJnSITMOvLI1O76cWIgw4DZPfpOxaiIPVibRqyJRhsCevLg2JOwxPC9ICFsKA6EDsKJEj767GTGvDImm00aSKJyrbaJYN6jE=","tag":"CdsHdKsRyKPZNy+WRoR7gg=="}"#.utf8),key:key,now:Date(timeIntervalSince1970:100))
        XCTAssertTrue(valid.eventOnly); XCTAssertTrue(valid.authorization.isEmpty); XCTAssertEqual(valid.xuid,"42")
        XCTAssertThrowsError(try PCTransfer.open(Data(#"{"schemaVersion":1,"nonce":"AAECAwQFBgcICQoL","ciphertext":"PCCleK2Ar3rbJOX42IYWT7n2thjQWTQVVgPHvz1LZcRkftqowKp39laIX8/w8kFczGNAr27kgfYftUtsbIuanJlGpw66vkhDJnSINq3DPcYIqrFfR1FgU4iT+7QIldr5rpx7yoRjrWz7ckrmJx5EEZhdQomb/33YAcx5/aKeUz+JYTu1zbm/O22VIcIQvzqnWIDBGRUz9jL8NpoSiZOdzKm3qSXZgg==","tag":"y+ZH/MOJecRuYCfprzupMw=="}"#.utf8),key:key,now:Date(timeIntervalSince1970:100)))
        XCTAssertThrowsError(try PCTransfer.open(Data(#"{"schemaVersion":1,"nonce":"AAECAwQFBgcICQoL","ciphertext":"PCCleK2Ar3rbJOX42IYWT7n2thjQWTQVVgPHvz1Lb8Zpddzeg+Ew4AHNG8+ypwoM3HtMrXi31q5X+FhwYoKBh59S5EDz8wRNPHbPGIrherxX4awMVFlzSoXHxo40w5e964Bk15Zy82nnckraLQFIHJcCFM6NsQe2bd0ytfKPHGvIE3Tkjvf8Yzic","tag":"s7w/ln/3AN6mawaQOBH2pw=="}"#.utf8),key:key,now:Date(timeIntervalSince1970:100)))
        XCTAssertThrowsError(try PCTransfer.open(Data(#"{"schemaVersion":1,"nonce":"AAECAwQFBgcICQoL","ciphertext":"PCCleK2Ar3rbJOX42IYWT7n2thjQWTQVVgPHvz1LZcRkftqowKp39laIX8/w8kFczGNAr27kgfYftUtsbIuanJlGpw66vkhDJnSITMOvLI1O76cWIgw4DZPfpOxa0pWt6Z0hls0zrWnfaByIaEIRT8c=","tag":"GF1KD7/q2z2Kd2X0+JG9+Q=="}"#.utf8),key:key,now:Date(timeIntervalSince1970:100)))
    }
    func testQRReassemblyAndIsolation() throws {
        let bytes = try JSONSerialization.data(withJSONObject:["key":String(repeating:"A",count:43),"file":Data("synthetic".utf8).base64EncodedString()])
        let digest = SHA256.hash(data:bytes).map { String(format:"%02x",$0) }.joined()
        let text = bytes.base64EncodedString(); let middle = text.index(text.startIndex,offsetBy:text.count/2)
        let frames = ["ALPC1|\(digest)|0|2|"+text[..<middle],"ALPC1|\(digest)|1|2|"+text[middle...]]
        var collector = QRTransferCollector()
        XCTAssertNil(try collector.accept(String(frames[1])))
        XCTAssertNil(try collector.accept(String(frames[1])))
        XCTAssertEqual(try collector.accept(String(frames[0]))?.0,Data("synthetic".utf8))
        XCTAssertThrowsError(try collector.accept("ALPC1|"+String(repeating:"b",count:64)+"|0|2|AAAA"))
        XCTAssertThrowsError(try collector.accept("ALPC1|\(digest)|0|300|AAAA"))
        var broken = QRTransferCollector()
        XCTAssertThrowsError(try broken.accept("ALPC1|"+String(repeating:"0",count:64)+"|0|1|"+text))
    }
    func testEncryptedTransferCompatibilityAndExpiry() throws {
        let data = Data(#"{"schemaVersion":1,"nonce":"AAECAwQFBgcICQoL","ciphertext":"PCCleK2Ar3rbJOX42IYWT7nnqxaIDjYYGl3HsS9LLJBgZdqUwLN74hXQFoLmpRIathssvnTmg6ICphgqI5CMgIRUow66sgRNPjHcC4H7WodT76dATEErUqW/0v9WwJn19slr1Z8ysXTqdFvGIRYMGswCDN/B6Qf/NY8+v+eVL3qZaDf2hPvpMn2VIM5PszajQp2MW3Bp4yP+Ops2jvDTgOb78mg=","tag":"zpeo91arlKCe4Kg2CWIRkA=="}"#.utf8)
        let key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"
        let transfer = try PCTransfer.open(data,key:key,now:Date(timeIntervalSince1970:100))
        XCTAssertEqual(transfer.xuid,"42"); XCTAssertEqual(transfer.eventToken,"x:XBL3.0 x=123;synthetic-event")
        XCTAssertThrowsError(try PCTransfer.open(data,key:key,now:Date(timeIntervalSince1970:701)))
        XCTAssertThrowsError(try PCTransfer.open(data,key:String(repeating:"A",count:43),now:Date(timeIntervalSince1970:100)))
        var corrupted = try JSON.object(data); corrupted["tag"] = Data(repeating:0,count:16).base64EncodedString()
        XCTAssertThrowsError(try PCTransfer.open(JSON.data(corrupted),key:key,now:Date(timeIntervalSince1970:100)))
    }
    func testMobileReleaseFilteringAndNumericVersion() throws {
        XCTAssertTrue(MobileRelease.newer("0.10.0",than:"0.9.9")); XCTAssertFalse(MobileRelease.newer("0.3.0",than:"0.3.0"))
        let releases: [[String:Any]] = [
          ["draft":false,"html_url":"https://github.com/Achievement-Labs/achievement-labs/releases/tag/v9.0.0","assets":[["name":"AchievementLabs.exe"]]],
          ["draft":false,"html_url":"https://github.com/Achievement-Labs/achievement-labs/releases/tag/ios-v0.4.0","assets":[["name":"AchievementLabs-iOS-0.4.0-UNSIGNED.ipa","browser_download_url":"https://github.com/Achievement-Labs/achievement-labs/releases/download/ios-v0.4.0/AchievementLabs-iOS-0.4.0-UNSIGNED.ipa"]]]
        ]
        XCTAssertEqual(try MobileRelease.latest(JSON.data(releases))?.version,"0.4.0")
        var oldRepository = releases[1]; oldRepository["html_url"] = "https://github.com/thevampirebat/achievement-labs/releases/tag/ios-v0.4.0"
        XCTAssertNil(try MobileRelease.latest(JSON.data([oldRepository])))
        var invalid = releases[1]; invalid["html_url"] = "https://example.com/releases/tag/x"
        XCTAssertNil(try MobileRelease.latest(JSON.data([invalid])))
    }
    func testFeedCannotOverwriteBundledAndRetainsCacheOnBadIdentity() throws {
        guard let path = ProcessInfo.processInfo.environment["LABS_CATALOGUE"] else { throw XCTSkip("Generated catalogue required") }
        let c = try Catalogue(folder:URL(fileURLWithPath:path))
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at:folder,withIntermediateDirectories:true); defer { try? FileManager.default.removeItem(at:folder) }
        let cache = folder.appendingPathComponent("events.json")
        let requests = MobileRecipes(recipes:["1":[[Replacement(target:"REPLACEID",value:"1")]]])
        let remote = SyncedTitle(template:#"{"data":{"baseData":{"titleId":"999999999","serviceConfigId":"00000000-0000-0000-0000-000000000001","id":REPLACEID}}}"#,recipes:requests)
        let feed = EventFeed(schemaVersion:1,upstreamRelease:"v1.0.8",archiveSHA256:String(repeating:"a",count:64),generatedAt:ISO8601DateFormatter().string(from:Date()),titles:["999999999":remote,"572802557":remote])
        let old = c.index["572802557"]!.count
        let importedPack = try JSONDecoder().decode(EventFeed.self,from:JSONEncoder().encode(feed))
        XCTAssertEqual(try c.merge(importedPack,cache:cache),1); XCTAssertEqual(c.index["572802557"]?.count,old)
        XCTAssertEqual(try c.merge(importedPack,cache:cache),0)
        XCTAssertFalse(try c.payloads(title:"999999999",achievement:"1",xuid:"0").isEmpty)
        let before = try Data(contentsOf:cache)
        let invalid = EventFeed(schemaVersion:1,upstreamRelease:"v1.0.8",archiveSHA256:String(repeating:"b",count:64),generatedAt:feed.generatedAt,titles:["888888888":remote])
        XCTAssertThrowsError(try c.merge(invalid,cache:cache)); XCTAssertEqual(try Data(contentsOf:cache),before)
        let restored = try Catalogue(folder:URL(fileURLWithPath:path)); try restored.loadCache(cache)
        XCTAssertNotNil(restored.index["999999999"])
        let local = c.index.first { $0.key != "999999999" && $0.value.reason == nil }!.key
        let id = try c.recipes(local).recipes.keys.first!
        _ = try c.payloads(title:local,achievement:id,xuid:"0")
        _ = try c.payloads(title:"999999999",achievement:"1",xuid:"0")
        XCTAssertNoThrow(try c.payloads(title:local,achievement:id,xuid:"0"))
    }
}
