import XCTest
@testable import LabsMobileCore
final class TransportTests: XCTestCase {
    func testLocaleDelayInputValidatesEntireValue() {
        XCTAssertEqual(QueueDelayInput.seconds("1.5",locale:Locale(identifier:"en_US")),90)
        XCTAssertEqual(QueueDelayInput.seconds("1,5",locale:Locale(identifier:"fr_FR")),90)
        for value in ["1x","NaN","-1","1.2.3","525601",""] { XCTAssertNil(QueueDelayInput.seconds(value,locale:Locale(identifier:"en_US"))) }
        XCTAssertEqual(QueueDelayInput.seconds("0"),0)
    }
    func testTLSDiagnosticsExcludeSecretsAndFullRequest() {
        let error = NSError(domain:NSURLErrorDomain,code:-1200,userInfo:[NSLocalizedDescriptionKey:"secret authorization", "_kCFStreamErrorCodeKey":-9806,NSURLErrorFailingURLErrorKey:URL(string:"https://example.invalid/?token=secret")!])
        let failure = XboxTransportFailure(error,endpoint:"mobile.events.data.microsoft.com")
        XCTAssertTrue(failure.isTLS); XCTAssertTrue(failure.localizedDescription.contains("-9806"))
        XCTAssertFalse(failure.localizedDescription.contains("secret")); XCTAssertFalse(failure.localizedDescription.contains("token="))
    }
    func testEventRouteUsesWindowsCollectorAndTLSFailureDoesNotReplay() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil }
        var count = 0
        LoginProtocol.handler = { request in
            count += 1; XCTAssertEqual(request.url?.host,"v20.events.data.microsoft.com")
            XCTAssertEqual(request.httpMethod,"POST")
            throw NSError(domain:NSURLErrorDomain,code:-1200)
        }
        var account = Account(authorization:"XBL3.0 x=1;synthetic",xuid:"42",gamertag:"Test"); account.eventToken = "x:XBL3.0 x=1;synthetic"
        do { try await XboxService(session:session).sendEvent("{}",account:account); XCTFail() }
        catch let failure as XboxTransportFailure { XCTAssertTrue(failure.isTLS) }
        XCTAssertEqual(count,1)
    }
}
