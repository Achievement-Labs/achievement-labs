import XCTest
@testable import LabsMobileCore

final class ServiceRefinementTests: XCTestCase {
    func testEventRequestCompactsTemplateAndPreservesValues() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config)
        defer { session.invalidateAndCancel(); LoginProtocol.handler = nil; LoginProtocol.responseCode = 200 }
        var sends = 0
        LoginProtocol.handler = { request in
            sends += 1
            XCTAssertEqual(request.url?.absoluteString,"https://v20.events.data.microsoft.com/OneCollector/1.0/")
            XCTAssertEqual(request.value(forHTTPHeaderField:"Content-Type"),"application/x-json-stream; charset=utf-8")
            XCTAssertEqual(request.value(forHTTPHeaderField:"tickets"),"\"1\"=\"x:XBL3.0 x=123;synthetic-event\"")
            XCTAssertEqual(request.value(forHTTPHeaderField:"authxtoken"),"XBL3.0 x=-;synthetic-account")
            var body = request.httpBody ?? Data()
            if let stream = request.httpBodyStream {
                stream.open(); defer { stream.close() }; var buffer = [UInt8](repeating:0,count:4096)
                while stream.hasBytesAvailable { let n = stream.read(&buffer,maxLength:buffer.count); if n <= 0 { break }; body.append(contentsOf:buffer.prefix(n)) }
            }
            let text = String(decoding:body,as:UTF8.self)
            XCTAssertFalse(text.contains("\n")); XCTAssertFalse(text.contains("\r"))
            let json = try JSON.object(body)
            XCTAssertEqual(json["label"] as? String,"two words\nsecond line")
            XCTAssertEqual(json["number"] as? Int64,10451802786899566)
            return Data(#"{"acc":1,"rej":0}"#.utf8)
        }
        var account = Account(authorization:"XBL3.0 x=123;synthetic-account",xuid:"42",gamertag:"Test")
        account.eventToken = "x:XBL3.0 x=123;synthetic-event"
        let pretty = "{\n  \"label\": \"two words\\nsecond line\",\n  \"number\": 10451802786899566\n}"
        try await XboxService(session:session).sendEvent(pretty,account:account)
        XCTAssertEqual(sends,1)
        // Invalid bodies must be rejected locally, without a collector request.
        do { try await XboxService(session:session).sendEvent("not JSON",account:account); XCTFail("Invalid JSON must fail") } catch { }
        XCTAssertEqual(sends,1)
    }
    func testSpoofingUsesDesktopContract() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil }
        LoginProtocol.handler = { request in
            XCTAssertEqual(request.url?.host,"presence-heartbeat.xboxlive.com")
            XCTAssertEqual(request.url?.absoluteString,"https://presence-heartbeat.xboxlive.com/users/xuid(42)/devices/current/")
            XCTAssertEqual(request.value(forHTTPHeaderField:"x-xbl-contract-version"),"3")
            var body = request.httpBody ?? Data()
            if let stream = request.httpBodyStream {
                stream.open(); defer { stream.close() }; var buffer = [UInt8](repeating:0,count:4096)
                while stream.hasBytesAvailable { let n = stream.read(&buffer,maxLength:buffer.count); if n <= 0 { break }; body.append(contentsOf:buffer.prefix(n)) }
            }
            if request.httpMethod == "POST" {
                let json = try JSON.object(body); let title = (json["titles"] as! [[String:Any]])[0]
                XCTAssertEqual(title["id"] as? String,"7"); XCTAssertEqual(title["expiration"] as? Int,600)
                XCTAssertEqual(title["state"] as? String,"active"); XCTAssertEqual(title["sandbox"] as? String,"RETAIL")
            } else { XCTAssertEqual(request.httpMethod,"DELETE"); XCTAssertTrue(body.isEmpty) }
            return Data()
        }
        let api = XboxService(session:session); let account = Account(authorization:"synthetic",xuid:"42",gamertag:"Test")
        try await api.presence("7",account:account); try await api.presence(nil,account:account)
    }
    func testReferenceReadUsesOwnAuthorizationAndReferenceXuid() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil }
        LoginProtocol.handler = { request in
            XCTAssertEqual(request.value(forHTTPHeaderField:"Authorization"),"synthetic-own")
            XCTAssertTrue(request.url!.path.contains("xuid(99)"))
            return try JSON.data(["achievements":[["id":"1","name":"Test","progressState":"Achieved","progression":["timeUnlocked":"2026-01-01T00:00:00Z"]]]])
        }
        let rows = try await XboxService(session:session).achievements(try Game(["titleId":"7"]),account:Account(authorization:"synthetic-own",xuid:"42",gamertag:"Test"),xuid:"99")
        XCTAssertNotNil(rows.first?.unlockedAt)
    }
    func testPresence403IsNotMisreportedAsExpiredLogin() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil; LoginProtocol.responseCode = 200 }
        LoginProtocol.responseCode = 403; LoginProtocol.handler = { _ in Data() }
        do {
            try await XboxService(session:session).presence("7",account:Account(authorization:"synthetic",xuid:"42",gamertag:"Test"))
            XCTFail("403 must fail")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("refused title spoofing"))
            XCTAssertFalse(error.localizedDescription.contains("expired"))
        }
    }

}

