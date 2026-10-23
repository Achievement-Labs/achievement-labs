import XCTest
@testable import LabsMobileCore

final class TitleSearchTests: XCTestCase {
    func testIDsBoundsAndEncodedPublicQuery() throws {
        XCTAssertEqual(try TitleSearch.direct(" 0007 ")?.id,"7")
        XCTAssertEqual(try TitleSearch.direct("4294967295")?.id,"4294967295")
        for value in ["0","4294967296","",String(repeating:"x",count:201)] { XCTAssertThrowsError(try TitleSearch.direct(value)) }
        XCTAssertNil(try TitleSearch.direct("Halo"))
        let url = try TitleSearch.url("Halo & Gears/测试")
        XCTAssertEqual(url.host,"dbox.tools"); XCTAssertTrue(url.absoluteString.hasPrefix("https://dbox.tools/api/title_ids/?"))
        XCTAssertEqual(URLComponents(url:url,resolvingAgainstBaseURL:false)?.queryItems?.first?.value,"Halo & Gears/测试")
    }
    func testPublicHexIDsPlatformsDuplicatesAndNoFakeProgress() throws {
        let rows: [[String:Any]] = [
            ["title_id":"0D174C79","name":"Halo 5","systems":["XBOXONE"]],
            ["title_id":"0D174C79","name":"Duplicate","systems":[]],
            ["title_id":"FFFFFFFF","name":"Legacy","systems":["PC","XBOX360"]],
            ["title_id":"00000000","name":"Invalid"], ["title_id":"../escape","name":"Invalid"]
        ]
        let games = try TitleSearch.parse(JSON.data(["items":rows]))
        XCTAssertEqual(games.count,2); XCTAssertEqual(games[0].id,String(UInt32("0D174C79",radix:16)!))
        XCTAssertEqual(games[0].platforms,["XboxOne"]); XCTAssertTrue(games[1].legacy)
        XCTAssertTrue(games.allSatisfy { $0.total == 0 && $0.unlocked == 0 && !$0.complete })
        XCTAssertThrowsError(try TitleSearch.parse(JSON.data(["other":[]])))
        XCTAssertThrowsError(try TitleSearch.parse(Data(repeating:32,count:2*1024*1024+1)))
    }
    func testSearchRequestIsPublicAndDirectIDNeedsNoNetwork() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [TitleSearchProtocol.self]
        let api = PublicSync(session:URLSession(configuration:config))
        let direct = try await api.searchTitles("7"); XCTAssertEqual(direct.first?.id,"7")
        let results = try await api.searchTitles("Halo & Gears")
        XCTAssertEqual(results.first?.id,"7")
    }
}

private final class TitleSearchProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        XCTAssertEqual(request.url?.host,"dbox.tools"); XCTAssertEqual(request.httpMethod,"GET")
        XCTAssertNil(request.value(forHTTPHeaderField:"Authorization")); XCTAssertNil(request.value(forHTTPHeaderField:"tickets"))
        XCTAssertNil(request.httpBody)
        XCTAssertEqual(URLComponents(url:request.url!,resolvingAgainstBaseURL:false)?.queryItems?.first?.value,"Halo & Gears")
        let data = Data(#"{"items":[{"title_id":"00000007","name":"Found title","systems":["XBOXONE"]}]}"#.utf8)
        client?.urlProtocol(self,didReceive:HTTPURLResponse(url:request.url!,statusCode:200,httpVersion:nil,headerFields:nil)!,cacheStoragePolicy:.notAllowed)
        client?.urlProtocol(self,didLoad:data); client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}
