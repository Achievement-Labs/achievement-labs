import XCTest
@testable import LabsMobileCore

final class MobilePresenceAuthorizationTests: XCTestCase {
    private func body(_ request:URLRequest)->Data {
        if let bytes=request.httpBody { return bytes }
        guard let stream=request.httpBodyStream else { return Data() };stream.open();defer { stream.close() }
        var bytes=Data(),buffer=[UInt8](repeating:0,count:4096)
        while stream.hasBytesAvailable { let n=stream.read(&buffer,maxLength:buffer.count);if n<=0 { break };bytes.append(contentsOf:buffer.prefix(n)) };return bytes
    }
    func testMobileExchangePreservesCredentialsAndRejectsMismatchesAndExpiry() async throws {
        for scenario in ["success","mismatch","expired","refused"] {
            let config=URLSessionConfiguration.ephemeral;config.protocolClasses=[LoginProtocol.self]
            let session=URLSession(configuration:config);defer { session.invalidateAndCancel();LoginProtocol.handler=nil;LoginProtocol.responseCode=200 }
            var before=Account(authorization:"XBL3.0 x=123;library",xuid:"42",gamertag:"Synthetic",eventToken:"x:XBL3.0 x=123;events")
            before.refreshToken="old";before.loginProfile=LoginProfile.ios.rawValue
            var rotated=false,endpoints=[String]()
            LoginProtocol.handler={ request in
                let host=request.url!.host!;endpoints.append(host);LoginProtocol.responseCode=200
                if host=="login.live.com" { return try JSON.data(["access_token":"synthetic-access","refresh_token":"next"]) }
                XCTAssertTrue(rotated)
                if host=="device.auth.xboxlive.com" { XCTAssertNotNil(request.value(forHTTPHeaderField:"Signature"));return try JSON.data(["Token":"device"]) }
                if host=="user.auth.xboxlive.com" { return try JSON.data(["Token":"user"]) }
                if host=="profile.xboxlive.com" { return try JSON.data(["profileUsers":[["id":scenario=="mismatch" ? "43":"42","settings":[["id":"Gamertag","value":"Synthetic"]]]]]) }
                XCTAssertEqual(host,"xsts.auth.xboxlive.com")
                XCTAssertEqual(try JSON.object(self.body(request))["RelyingParty"] as? String,"http://xboxlive.com")
                if scenario=="refused" { LoginProtocol.responseCode=403;return Data("synthetic-access never log".utf8) }
                return try JSON.data(["Token":"presence","NotAfter":scenario=="expired" ? "2000-01-01T00:00:00Z":"2099-01-01T00:00:00Z","DisplayClaims":["xui":[["uhs":"123","xid":"42"]]]])
            }
            do {
                let result=try await BrowserLogin(session:session,profile:.ios).acquirePresenceAuthorization(before) { value in
                    XCTAssertEqual(value.refreshToken,"next");XCTAssertEqual(value.authorization,before.authorization);XCTAssertEqual(value.eventToken,before.eventToken);rotated=true
                }
                XCTAssertEqual(scenario,"success");XCTAssertEqual(result.presenceAuthorization,"XBL3.0 x=123;presence")
                XCTAssertEqual(result.authorization,before.authorization);XCTAssertEqual(result.eventToken,before.eventToken);XCTAssertFalse(result.needsPresenceAuthorization())
            } catch { XCTAssertNotEqual(scenario,"success");XCTAssertFalse(error.localizedDescription.contains("synthetic-access")) }
            XCTAssertNil(before.presenceAuthorization);XCTAssertFalse(endpoints.contains { $0.contains("events") || $0.contains("presence-heartbeat") || $0.contains("achievements") })
        }
    }
    func testOldAccountsDecodeAndRequireNewPresenceAuthorization() throws {
        let old=Data(#"{"authorization":"XBL3.0 x=1;old","xuid":"42","gamertag":"Synthetic","eventToken":""}"#.utf8)
        var account=try JSONDecoder().decode(Account.self,from:old);XCTAssertTrue(account.needsPresenceAuthorization())
        account.presenceAuthorization="XBL3.0 x=1;presence";account.presenceAuthorizationExpiresAt=Date().addingTimeInterval(299);XCTAssertTrue(account.needsPresenceAuthorization())
        account.presenceAuthorizationExpiresAt=Date().addingTimeInterval(3600);XCTAssertFalse(account.needsPresenceAuthorization())
    }
    func testHeartbeatAndStopUsePresenceCredentialWithoutEventToken() async throws {
        let config=URLSessionConfiguration.ephemeral;config.protocolClasses=[LoginProtocol.self]
        let session=URLSession(configuration:config);defer { session.invalidateAndCancel();LoginProtocol.handler=nil;LoginProtocol.responseCode=200 }
        var account=Account(authorization:"XBL3.0 x=123;library",xuid:"42",gamertag:"Synthetic")
        account.presenceAuthorization="XBL3.0 x=123;presence"
        var methods=[String]()
        LoginProtocol.handler={ request in
            XCTAssertEqual(request.url?.host,"presence-heartbeat.xboxlive.com")
            XCTAssertEqual(request.value(forHTTPHeaderField:"Authorization"),account.presenceAuthorization)
            methods.append(request.httpMethod!);return Data("{}".utf8)
        }
        let service=XboxService(session:session);try await service.presence("7",account:account);try await service.presence(nil,account:account)
        XCTAssertEqual(methods,["POST","DELETE"])
    }
}
