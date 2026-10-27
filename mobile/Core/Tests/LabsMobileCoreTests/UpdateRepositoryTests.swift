import XCTest
@testable import LabsMobileCore

final class UpdateRepositoryTests: XCTestCase {
    func testForkAppAndUpstreamEventChecksHaveNoAccountHeaders() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [UpdateRepositoryProtocol.self]
        let updates = PublicSync(session:URLSession(configuration:config))
        let release = try await updates.release(); XCTAssertNil(release)
        let events = try await updates.events(); XCTAssertEqual(events.schemaVersion,1)
    }
}
private final class UpdateRepositoryProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        XCTAssertEqual(request.httpMethod,"GET"); XCTAssertNil(request.httpBody)
        XCTAssertNil(request.value(forHTTPHeaderField:"Authorization")); XCTAssertNil(request.value(forHTTPHeaderField:"tickets"))
        let data: Data
        if request.url?.absoluteString == "https://api.github.com/repos/Achievement-Labs/achievement-labs/releases?per_page=100" { data = Data("[]".utf8) }
        else {
            XCTAssertEqual(request.url?.absoluteString,"https://raw.githubusercontent.com/Achievement-Labs/achievement-labs/mobile-event-feed/catalogue.json")
            data = Data(#"{"schemaVersion":1,"upstreamRelease":"v1.0.8","archiveSHA256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","generatedAt":"2026-10-23T00:00:00Z","titles":{}}"#.utf8)
        }
        client?.urlProtocol(self,didReceive:HTTPURLResponse(url:request.url!,statusCode:200,httpVersion:nil,headerFields:nil)!,cacheStoragePolicy:.notAllowed)
        client?.urlProtocol(self,didLoad:data); client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}
