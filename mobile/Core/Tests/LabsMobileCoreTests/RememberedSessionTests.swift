import XCTest
@testable import LabsMobileCore

final class RememberedSessionTests: XCTestCase {
    private func body(_ request: URLRequest) -> Data {
        if let data = request.httpBody { return data }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open(); defer { stream.close() }; var data = Data(); var buffer = [UInt8](repeating:0,count:4096)
        while stream.hasBytesAvailable { let n = stream.read(&buffer,maxLength:buffer.count); if n <= 0 { break }; data.append(contentsOf:buffer.prefix(n)) }; return data
    }
    private func fixture() -> Account {
        var account = Account(authorization:"XBL3.0 x=123;old",xuid:"42",gamertag:"Synthetic",eventToken:"synthetic-event")
        account.refreshToken = "old-refresh"; account.loginProfile = LoginProfile.desktop.rawValue
        account.authorizationExpiresAt = Date(timeIntervalSince1970:1); return account
    }
    func testRotationPrecedesXboxExchangeAndPreservesEventToken() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil; LoginProtocol.responseCode = 200 }
        var rotated: Account?
        LoginProtocol.handler = { request in
            switch request.url!.host! {
            case "login.live.com":
                let q = URLComponents(string:"https://example.invalid/?" + String(decoding:self.body(request),as:UTF8.self))!.queryItems!
                XCTAssertEqual(q.first { $0.name == "grant_type" }?.value,"refresh_token")
                XCTAssertEqual(q.first { $0.name == "refresh_token" }?.value,"old-refresh")
                XCTAssertEqual(q.first { $0.name == "client_id" }?.value,LoginProfile.desktop.clientID)
                return try JSON.data(["access_token":"renewed","refresh_token":"rotated-refresh"])
            case "device.auth.xboxlive.com":
                XCTAssertEqual(rotated?.refreshToken,"rotated-refresh"); XCTAssertEqual(rotated?.authorization,"XBL3.0 x=123;old")
                return try JSON.data(["Token":"synthetic-device"])
            case "sisu.xboxlive.com":
                return try JSON.data(["AuthorizationToken":["Token":"new-xbox","NotAfter":"2099-01-01T00:00:00Z","DisplayClaims":["xui":[["uhs":"123"]]]]])
            case "profile.xboxlive.com":
                return try JSON.data(["profileUsers":[["id":"42","settings":[["id":"Gamertag","value":"Synthetic"]]]]])
            default: XCTFail("Unexpected network request"); return Data()
            }
        }
        let account = try await BrowserLogin(session:session,profile:.desktop).refresh(fixture()) { rotated = $0 }
        XCTAssertEqual(account.refreshToken,"rotated-refresh"); XCTAssertEqual(account.eventToken,"synthetic-event")
        XCTAssertEqual(account.xuid,"42"); XCTAssertEqual(account.authorization,"XBL3.0 x=123;new-xbox"); XCTAssertFalse(account.needsRefresh())
    }
    func testRotationSurvivesLaterXboxFailure() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil; LoginProtocol.responseCode = 200 }
        var rotated: Account?
        LoginProtocol.handler = { request in
            if request.url?.host == "login.live.com" { return try JSON.data(["access_token":"renewed","refresh_token":"rotated-refresh"]) }
            LoginProtocol.responseCode = 503; return Data()
        }
        do { _ = try await BrowserLogin(session:session,profile:.desktop).refresh(fixture()) { rotated = $0 }; XCTFail("Xbox failure should propagate") } catch { }
        XCTAssertEqual(rotated?.refreshToken,"rotated-refresh"); XCTAssertEqual(rotated?.authorization,fixture().authorization)
        XCTAssertEqual(rotated?.xuid,"42"); XCTAssertEqual(rotated?.eventToken,"synthetic-event")
    }
    func testWrongAccountRenewalIsRejected() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil; LoginProtocol.responseCode = 200 }
        LoginProtocol.handler = { request in
            switch request.url?.host {
            case "login.live.com": return try JSON.data(["access_token":"renewed","refresh_token":"rotated"])
            case "device.auth.xboxlive.com": return try JSON.data(["Token":"device"])
            case "sisu.xboxlive.com": return try JSON.data(["AuthorizationToken":["Token":"other","DisplayClaims":["xui":[["uhs":"123"]]]]])
            default: return try JSON.data(["profileUsers":[["id":"99","settings":[]]]])
            }
        }
        do { _ = try await BrowserLogin(session:session,profile:.desktop).refresh(fixture()) { _ in }; XCTFail("Different XUID must not replace the account") }
        catch { XCTAssertTrue(error.localizedDescription.contains("different Xbox account")) }
    }
    func testPersistenceFailureStopsBeforeXboxExchange() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil; LoginProtocol.responseCode = 200 }
        var requests = 0
        LoginProtocol.handler = { request in
            requests += 1; XCTAssertEqual(request.url?.host,"login.live.com")
            return try JSON.data(["access_token":"renewed","refresh_token":"rotated"])
        }
        do { _ = try await BrowserLogin(session:session,profile:.desktop).refresh(fixture()) { _ in throw LabsError.message("Synthetic storage failure") }; XCTFail("Storage failure must stop renewal") } catch { }
        XCTAssertEqual(requests,1)
    }
    func testDiagnosticExcludesRawTokenLikeValues() throws {
        let url = URL(string:"https://presence-heartbeat.xboxlive.com/")!
        let response = HTTPURLResponse(url:url,statusCode:403,httpVersion:nil,headerFields:["ms-cv":"safe.correlation.1"])!
        let failure = XboxHTTPFailure(status:403,endpoint:url.host!,message:"Refused",response:response,data:try JSON.data(["XErr":2148916233,"message":"XBL3.0 x=secret;opaque","token":"secret"]))
        XCTAssertEqual(failure.serverCode,"2148916233"); XCTAssertFalse(failure.diagnostic.contains("secret")); XCTAssertTrue(failure.diagnostic.contains("safe.correlation.1"))
        let unsafe = XboxHTTPFailure(status:403,endpoint:url.host!,message:"Refused",response:response,data:try JSON.data(["code":"XBL3.0 x=secret;opaque"]))
        XCTAssertNil(unsafe.serverCode)
    }
}
