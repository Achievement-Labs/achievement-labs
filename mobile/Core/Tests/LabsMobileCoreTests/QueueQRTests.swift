import XCTest
import CryptoKit
@testable import LabsMobileCore
final class QueueQRTests: XCTestCase {
    func testQueueQRReassemblyAndIsolation() throws {
        let json = #"{"schemaVersion":1,"kind":"AchievementLabsQueueTransfer","ownerXuid":"42","games":[{"titleId":"7","name":"Unicode 🎮","items":[{"achievementId":"1","name":"First","delaySeconds":120,"remainingDelaySeconds":25,"status":"Waiting"}]}]}"#
        var large = try JSONDecoder().decode(QueueTransfer.self,from:Data(json.utf8))
        for i in 2...30 { large.games[0].items.append(TransferAchievement(achievementId:String(i),name:"Unicode 🎮 "+String(i),delaySeconds:120,remainingDelaySeconds:nil,status:"Waiting")) }
        let many = try QueueQRTransfer.frames(JSONEncoder().encode(large),owner:"42")
        XCTAssertGreaterThan(many.count,1)
        var multiple = QRTransferCollector(queueOnly:true)
        var assembled: Data?
        for frame in many.reversed() { if let value = try multiple.accept(frame) { assembled = value.0 }; _ = try multiple.accept(frame) }
        XCTAssertEqual(try JSONDecoder().decode(QueueTransfer.self,from:XCTUnwrap(assembled)).games[0].items.count,30)
        let frames = try QueueQRTransfer.frames(Data(json.utf8),owner:"42")
        var collector = QRTransferCollector(queueOnly:true)
        var result: Data?
        for frame in frames.reversed() { if let value = try collector.accept(frame) { result = value.0; XCTAssertEqual(value.1,"") } }
        let transfer = try JSONDecoder().decode(QueueTransfer.self,from:XCTUnwrap(result))
        try transfer.validate(owner:"42")
        XCTAssertEqual(transfer.games[0].items[0].remainingDelaySeconds,25)
        XCTAssertThrowsError(try transfer.validate(owner:"43"))
        var signInScanner = QRTransferCollector()
        XCTAssertThrowsError(try signInScanner.accept(frames[0]))
        var broken = QRTransferCollector(queueOnly:true)
        XCTAssertThrowsError(try broken.accept("ALQ1|"+String(repeating:"0",count:64)+"|0|1|e30="))
        XCTAssertThrowsError(try QueueQRTransfer.frames(Data(json.utf8),owner:"43"))
    }
    func testAutomaticScannerRecognisesQueuesAndCredentialsWithoutMixingFrames() throws {
        let json = #"{"schemaVersion":1,"kind":"AchievementLabsQueueTransfer","ownerXuid":"42","games":[{"titleId":"7","name":"Game","items":[{"achievementId":"1","name":"First","delaySeconds":120,"status":"Waiting"}]}]}"#
        var automatic = QRTransferCollector(autoDetect:true)
        var queue: Data?
        for frame in try QueueQRTransfer.frames(Data(json.utf8),owner:"42") { if let result = try automatic.accept(frame) { queue = result.0; XCTAssertTrue(result.1.isEmpty) } }
        let transfer = try JSONDecoder().decode(QueueTransfer.self,from:XCTUnwrap(queue)); try transfer.validate(owner:"42")
        XCTAssertThrowsError(try transfer.validate(owner:"43"))
        let bytes = try JSONSerialization.data(withJSONObject:["key":String(repeating:"A",count:43),"file":Data("synthetic".utf8).base64EncodedString()])
        let hash = SHA256.hash(data:bytes).map { String(format:"%02x",$0) }.joined()
        let text = bytes.base64EncodedString(); let middle = text.index(text.startIndex,offsetBy:text.count/2)
        let first = "ALPC1|\(hash)|0|2|"+text[..<middle], last = "ALPC1|\(hash)|1|2|"+text[middle...]
        var signIn = QRTransferCollector(autoDetect:true)
        XCTAssertNil(try signIn.accept(first))
        XCTAssertThrowsError(try signIn.accept(last.replacingOccurrences(of:"ALPC1|",with:"ALQ1|")))
        let credentials = try XCTUnwrap(signIn.accept(last)); XCTAssertEqual(credentials.0,Data("synthetic".utf8)); XCTAssertEqual(credentials.1.count,43)
        var queueOnly = QRTransferCollector(queueOnly:true,autoDetect:true)
        XCTAssertThrowsError(try queueOnly.accept(first))
        var unknown = QRTransferCollector(autoDetect:true)
        XCTAssertThrowsError(try unknown.accept(first.replacingOccurrences(of:"ALPC1|",with:"UNKNOWN|")))
    }
    func testSignInOnlyCSharpCompatibleVector() throws {
        let data = Data(#"{"schemaVersion":1,"nonce":"AAECAwQFBgcICQoL","ciphertext":"PCCleK2Ar3rbJOX42IYWT7nnqxabEjEYGl3H9nQObvtvMoLe17R7/FaeXdm6pQQajywU5TWkyqBe40N2dsHPzKh+ikn94QYZIWWYXdT8d4ZM4qwWHwB+CZ6e8bkWhJuh6Z0vg8o1nHX1eVCQclcDU5gCGtuKt0DpDIt197XWXiKZM3Wy3Li5OnKAPMhStAm6Rp2RUEZQ52i2MZ0blq8=","tag":"2FgnXkL3zIZxo5ImJtQlhA=="}"#.utf8)
        let transfer = try PCTransfer.open(data,key:"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",now:Date(timeIntervalSince1970:100))
        XCTAssertTrue(transfer.signInOnly); XCTAssertFalse(transfer.eventOnly); XCTAssertTrue(transfer.eventToken.isEmpty)
        XCTAssertEqual(transfer.authorization,"XBL3.0 x=123;synthetic-account")
        XCTAssertThrowsError(try PCTransfer.open(data,key:"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",now:Date(timeIntervalSince1970:701)))
    }
}
