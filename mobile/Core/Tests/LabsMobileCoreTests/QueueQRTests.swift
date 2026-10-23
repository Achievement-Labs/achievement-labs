import XCTest
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
    func testSignInOnlyCSharpCompatibleVector() throws {
        let data = Data(#"{"schemaVersion":1,"nonce":"AAECAwQFBgcICQoL","ciphertext":"PCCleK2Ar3rbJOX42IYWT7nnqxabEjEYGl3H9nQObvtvMoLe17R7/FaeXdm6pQQajywU5TWkyqBe40N2dsHPzKh+ikn94QYZIWWYXdT8d4ZM4qwWHwB+CZ6e8bkWhJuh6Z0vg8o1nHX1eVCQclcDU5gCGtuKt0DpDIt197XWXiKZM3Wy3Li5OnKAPMhStAm6Rp2RUEZQ52i2MZ0blq8=","tag":"2FgnXkL3zIZxo5ImJtQlhA=="}"#.utf8)
        let transfer = try PCTransfer.open(data,key:"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",now:Date(timeIntervalSince1970:100))
        XCTAssertTrue(transfer.signInOnly); XCTAssertFalse(transfer.eventOnly); XCTAssertTrue(transfer.eventToken.isEmpty)
        XCTAssertEqual(transfer.authorization,"XBL3.0 x=123;synthetic-account")
        XCTAssertThrowsError(try PCTransfer.open(data,key:"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",now:Date(timeIntervalSince1970:701)))
    }
}
