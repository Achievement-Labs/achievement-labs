import XCTest
@testable import LabsMobileCore

final class TitleSearchTests: XCTestCase {
    func testNumeralAliasesAndPlatformGroups() throws {
        XCTAssertTrue(try TitleSearch.variants("Black Ops 3").contains("Black Ops III"))
        XCTAssertTrue(try TitleSearch.variants("Black Ops iii").contains("Black Ops 3"))
        XCTAssertTrue(try TitleSearch.variants("Final Fantasy 13-2").contains("Final Fantasy XIII-2"))
        XCTAssertEqual(try TitleSearch.variants("I Am Alive"),["I Am Alive"])
        XCTAssertEqual(try TitleSearch.variants("Civilization MIXED 007").count,1)
        XCTAssertLessThanOrEqual(try TitleSearch.variants("Game 1 2 3 4 5 6 7 8").count,8)
        XCTAssertTrue(TitleSearch.matches("Call of Duty: Black Ops III",query:"black ops 3"))
        XCTAssertFalse(TitleSearch.matches("Black Ops II",query:"Black Ops 3"))
        XCTAssertEqual(TitlePlatform.groups(["PC","Xbox360"]),[.gfwl])
        XCTAssertEqual(TitlePlatform.groups(["Xbox One","PC"]),[.one,.pc])
        XCTAssertEqual(TitlePlatform.groups([]),[.other])
        XCTAssertEqual(TitlePlatform.groups(["SCARLETT"]),[.series])
        XCTAssertEqual(TitlePlatform.groups(["Windows 8"]),[.win8])
    }
    func testAliasRequestsMergeEditionsAndReportPartialFailures() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [NumeralSearchProtocol.self]
        let api = PublicSync(session:URLSession(configuration:config))
        let result = try await api.searchTitleResults("Black Ops 3")
        XCTAssertEqual(Set(result.games.map(\.id)),["7","8"]); XCTAssertFalse(result.partial)
        XCTAssertTrue(result.games.allSatisfy { $0.name == "Black Ops III" })
        let merged = TitleSearch.merge([result.games,result.games]); XCTAssertEqual(merged.count,2)
        let partial = try await api.searchTitleResults("Partial 3")
        XCTAssertTrue(partial.partial); XCTAssertEqual(partial.games.count,2)
        do { _ = try await api.searchTitleResults("Unavailable 3"); XCTFail("All failed requests must surface") } catch { }
        let direct = try await api.searchTitleResults("7")
        XCTAssertEqual(direct.games.first?.platforms,[]); XCTAssertFalse(direct.partial)
    }
    func testDiscoveredCountsUseAchievementRowsAndRetainEmptyResponses() throws {
        let original = try TitleSearch.direct("7")!
        let rows = try [Achievement(["id":"1","progressState":"Achieved"]),Achievement(["id":"2","progressState":"NotStarted"])]
        let loaded = TitleSearch.applyingProgress(original,rows:rows)
        XCTAssertEqual(loaded.unlocked,1); XCTAssertEqual(loaded.total,2)
        XCTAssertEqual(original.total,0); XCTAssertEqual(TitleSearch.applyingProgress(loaded,rows:[]),loaded)
    }
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

private final class NumeralSearchProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        XCTAssertNil(request.value(forHTTPHeaderField:"Authorization")); XCTAssertNil(request.value(forHTTPHeaderField:"tickets"))
        let query = URLComponents(url:request.url!,resolvingAgainstBaseURL:false)?.queryItems?.first?.value ?? ""
        let failed = query.hasPrefix("Unavailable") || query == "Partial III"
        let body = query == "Black Ops 3" ? #"{"items":[]}"# : #"{"items":[{"title_id":"7","name":"Black Ops III","systems":["XBOXONE"]},{"title_id":"8","name":"Black Ops III","systems":["PC"]}]}"#
        client?.urlProtocol(self,didReceive:HTTPURLResponse(url:request.url!,statusCode:failed ? 503 : 200,httpVersion:nil,headerFields:nil)!,cacheStoragePolicy:.notAllowed)
        client?.urlProtocol(self,didLoad:Data(body.utf8)); client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
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
