import XCTest
@testable import LabsMobileCore
final class QueuePolicyTests: XCTestCase {
 func item(_ id:String,_ status:String) throws -> QueueItem { var value=QueueItem(game:try Game(["titleId":"7","name":"Game"]),achievement:try Achievement(["id":id,"name":"Achievement"]),delay:120);value.status=status;return value }
 func testReviewStopsBeforeLaterWaitingAndNeverResendsImplicitly() throws {
  let queue=try [item("1","Unconfirmed — connection interrupted"),item("2","Waiting")]
  XCTAssertEqual(QueuePolicy.next(queue,stopOnFailure:true),.review(0));XCTAssertEqual(QueuePolicy.next(queue,stopOnFailure:false),.send(1));XCTAssertFalse(QueuePolicy.terminal(queue[0].status))
  XCTAssertEqual(QueuePolicy.next(try [item("1","Skipped by user"),item("2","Waiting")],stopOnFailure:true),.send(1))
 }
 func testPortableTransferRoundTripOwnerAndBounds() throws {
  var item=try item("1","Waiting");item.remainingDelay=23
  let transfer=QueueTransfer(ownerXuid:"42",games:[TransferGame(SavedGameQueue(items:[item]))])
  let data=try JSONEncoder().encode(transfer);XCTAssertFalse(String(decoding:data,as:UTF8.self).contains("authorization"))
  var restored=try JSONDecoder().decode(QueueTransfer.self,from:data);try restored.validate(owner:"42");XCTAssertEqual(restored.games[0].items[0].remainingDelaySeconds,23);XCTAssertThrowsError(try restored.validate(owner:"43"))
  restored.games[0].items[0].delaySeconds = -1;XCTAssertThrowsError(try restored.validate(owner:"42"))
  XCTAssertTrue(QueueTransfer.importedStatus("Accepted",unlocked:false).hasPrefix("Needs review"));XCTAssertEqual(QueueTransfer.importedStatus("Failed",unlocked:true),"Already unlocked")
 }
}
