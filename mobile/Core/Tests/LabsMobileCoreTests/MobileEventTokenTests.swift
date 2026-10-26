import XCTest
@testable import LabsMobileCore

final class MobileEventTokenTests: XCTestCase {
    private func body(_ request: URLRequest) -> Data {
        if let data = request.httpBody { return data }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open(); defer { stream.close() }; var data = Data(); var buffer = [UInt8](repeating:0,count:4096)
        while stream.hasBytesAvailable { let n = stream.read(&buffer,maxLength:buffer.count); if n <= 0 { break }; data.append(contentsOf:buffer.prefix(n)) }; return data
    }
    private func grant(_ token: String, hash: String = "123", xuid: String = "42", expiry: String = "2099-01-01T00:00:00Z") -> [String:Any] {
        ["Token":token,"NotAfter":expiry,"DisplayClaims":["xui":[["uhs":hash,"xid":xuid]]]]
    }
    func testIssuanceRefusalAndIdentityGuardsWithoutEventSubmission() async throws {
        for scenario in ["success","profile-mismatch","hash-mismatch","xuid-mismatch","expired","missing-expiry","refused"] {
            let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
            let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil; LoginProtocol.responseCode = 200 }
            var before = Account(authorization:"XBL3.0 x=123;old",xuid:"42",gamertag:"Synthetic",eventToken:"x:XBL3.0 x=123;old-event")
            before.refreshToken = "old-refresh"; before.loginProfile = LoginProfile.ios.rawValue
            var rotated = false; var endpoints = [String](); var requestedEvents = false
            LoginProtocol.handler = { request in
                let url = request.url!; endpoints.append(url.host! + url.path); LoginProtocol.responseCode = 200
                if url.host == "login.live.com" {
                    XCTAssertTrue(String(decoding:self.body(request),as:UTF8.self).contains("refresh_token"))
                    return try JSON.data(["access_token":"private-access","refresh_token":"next-refresh"])
                }
                XCTAssertTrue(rotated)
                if url.host == "profile.xboxlive.com" {
                    return try JSON.data(["profileUsers":[["id":scenario == "profile-mismatch" ? "43":"42","settings":[["id":"Gamertag","value":"Synthetic"]]]]])
                }
                let object = try JSON.object(self.body(request))
                if url.host == "device.auth.xboxlive.com" {
                    XCTAssertEqual((object["Properties"] as? [String:Any])?["DeviceType"] as? String,"iOS")
                    XCTAssertNotNil(request.value(forHTTPHeaderField:"Signature"))
                    return try JSON.data(["Token":"device"])
                }
                if url.host == "user.auth.xboxlive.com" { return try JSON.data(["Token":"user"]) }
                XCTAssertEqual(url.host,"xsts.auth.xboxlive.com")
                if object["RelyingParty"] as? String == "http://events.xboxlive.com" {
                    requestedEvents = true
                    if scenario == "refused" { LoginProtocol.responseCode = 403; return Data("private-access must-not-leak".utf8) }
                    var response = self.grant("issued-event",hash:scenario == "hash-mismatch" ? "999":"123",xuid:scenario == "xuid-mismatch" ? "43":"42",expiry:scenario == "expired" ? "2000-01-01T00:00:00Z":"2099-01-01T00:00:00Z")
                    if scenario == "missing-expiry" { response.removeValue(forKey:"NotAfter") }
                    return try JSON.data(response)
                }
                return try JSON.data(self.grant("identity"))
            }
            do {
                let result = try await BrowserLogin(session:session,profile:.ios).acquireEventToken(before) { account in
                    XCTAssertEqual(account.refreshToken,"next-refresh"); XCTAssertEqual(account.eventToken,before.eventToken); rotated = true
                }
                XCTAssertEqual(scenario,"success"); XCTAssertEqual(result.eventToken,"x:XBL3.0 x=123;issued-event"); XCTAssertEqual(result.refreshToken,"next-refresh")
            } catch {
                XCTAssertNotEqual(scenario,"success"); XCTAssertFalse(error.localizedDescription.contains("private-access"))
                if scenario == "refused" { XCTAssertTrue(error.localizedDescription.contains("HTTP 403")) }
            }
            XCTAssertTrue(rotated); XCTAssertEqual(before.eventToken,"x:XBL3.0 x=123;old-event")
            if scenario == "profile-mismatch" { XCTAssertFalse(requestedEvents) }
            XCTAssertFalse(endpoints.contains { $0.contains("v20.events") || $0.contains("achievements.xboxlive") || $0.contains("presence-heartbeat") })
        }
    }
    func testImportedSessionCannotStartProbe() async throws {
        var account = Account(authorization:"XBL3.0 x=123;old",xuid:"42",gamertag:"Synthetic")
        account.loginProfile = LoginProfile.ios.rawValue
        do { _ = try await BrowserLogin(profile:.ios).acquireEventToken(account) { _ in XCTFail("No rotation expected") }; XCTFail("Missing native refresh must be rejected") } catch { }
    }
    func testRotationStorageFailureStopsBeforeXboxRequests() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil; LoginProtocol.responseCode = 200 }
        var account = Account(authorization:"XBL3.0 x=123;old",xuid:"42",gamertag:"Synthetic"); account.loginProfile = LoginProfile.ios.rawValue; account.refreshToken = "old-refresh"
        var count = 0
        LoginProtocol.handler = { request in count += 1; XCTAssertEqual(request.url?.host,"login.live.com"); return try JSON.data(["access_token":"private-access","refresh_token":"next-refresh"]) }
        do { _ = try await BrowserLogin(session:session,profile:.ios).acquireEventToken(account) { _ in throw LabsError.message("Protected storage unavailable") }; XCTFail("Storage failure must stop the test") } catch { }
        XCTAssertEqual(count,1)
    }
}
