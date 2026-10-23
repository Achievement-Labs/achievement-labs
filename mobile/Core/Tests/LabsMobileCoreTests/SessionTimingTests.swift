import XCTest
@testable import LabsMobileCore
final class SessionTimingTests:XCTestCase {
 func testSpoofDoesNotStopAt45SecondsAndRenewsFromAcceptedHeartbeat() {
  let start=Date(timeIntervalSince1970:1000)
  let expiry=SessionTiming.spoofExpiry(lastHeartbeat:start,started:start)!
  XCTAssertEqual(expiry.timeIntervalSince(start),600)
  XCTAssertGreaterThan(expiry,start.addingTimeInterval(45))
  XCTAssertEqual(SessionTiming.spoofExpiry(lastHeartbeat:start.addingTimeInterval(240),started:start),start.addingTimeInterval(840))
 }
 func testNextItemDeadlineReplacesPriorActionAndHasNo45SecondLease() {
  let now=Date(timeIntervalSince1970:1000),next=Date(timeIntervalSince1970:1120)
  let first=SessionTiming.staleDate(now:now,spoofExpiry:nil,nextAction:next,queueRunning:true)
  XCTAssertEqual(first,next.addingTimeInterval(120))
  let second=next.addingTimeInterval(300)
  XCTAssertEqual(SessionTiming.staleDate(now:next,spoofExpiry:nil,nextAction:second,queueRunning:true),second.addingTimeInterval(120))
 }
 func testIdleAndCombinedSessionsUseTheirRealDeadlines() {
  let now=Date(timeIntervalSince1970:1000)
  XCTAssertNil(SessionTiming.spoofExpiry(lastHeartbeat:nil,started:nil))
  XCTAssertEqual(SessionTiming.staleDate(now:now,spoofExpiry:nil,nextAction:nil,queueRunning:false),now)
  XCTAssertEqual(SessionTiming.staleDate(now:now,spoofExpiry:now.addingTimeInterval(600),nextAction:now.addingTimeInterval(30),queueRunning:true),now.addingTimeInterval(150))
 }
}
