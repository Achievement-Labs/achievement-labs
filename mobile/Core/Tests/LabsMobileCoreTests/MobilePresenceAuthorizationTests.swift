import XCTest
import CryptoKit
@testable import LabsMobileCore

final class MobilePresenceAuthorizationTests: XCTestCase {
    private func body(_ request:URLRequest)->Data {
        if let bytes=request.httpBody { return bytes }
        guard let stream=request.httpBodyStream else { return Data() };stream.open();defer { stream.close() }
        var bytes=Data(),buffer=[UInt8](repeating:0,count:4096)
        while stream.hasBytesAvailable { let n=stream.read(&buffer,maxLength:buffer.count);if n<=0 { break };bytes.append(contentsOf:buffer.prefix(n)) };return bytes
    }
    func testMobileExchangePreservesCredentialsAndRejectsMismatchesAndExpiry() async throws {
        for profile in LoginProfile.allCases {
        for scenario in ["success","mismatch","expired","refused","missing-title","empty-title","title-refused"] {
            let config=URLSessionConfiguration.ephemeral;config.protocolClasses=[LoginProtocol.self]
            let session=URLSession(configuration:config);defer { session.invalidateAndCancel();LoginProtocol.handler=nil;LoginProtocol.responseCode=200 }
            var before=Account(authorization:"XBL3.0 x=123;library",xuid:"42",gamertag:"Synthetic",eventToken:"x:XBL3.0 x=123;events")
            before.refreshToken="old";before.loginProfile=profile.rawValue
            var rotated=false,endpoints=[String](),proof=[String:String]()
            LoginProtocol.handler={ request in
                let host=request.url!.host!;endpoints.append(host);LoginProtocol.responseCode=200
                if host=="login.live.com" {
                    XCTAssertTrue(String(data:self.body(request),encoding:.utf8)!.contains(profile.clientID))
                    return try JSON.data(["access_token":"synthetic-access","refresh_token":"next"])
                }
                XCTAssertTrue(rotated)
                if host=="profile.xboxlive.com" { return try JSON.data(["profileUsers":[["id":scenario=="mismatch" ? "43":"42","settings":[["id":"Gamertag","value":"Synthetic"]]]]]) }
                XCTAssertNotNil(request.value(forHTTPHeaderField:"Signature"))
                let payload=try JSON.object(self.body(request))
                if host=="device.auth.xboxlive.com" {
                    let properties=try XCTUnwrap(payload["Properties"] as? [String:Any]);XCTAssertEqual(properties["DeviceType"] as? String,profile.deviceType)
                    proof=try XCTUnwrap(properties["ProofKey"] as? [String:String]);return try JSON.data(["Token":"device"])
                }
                if host=="sisu.xboxlive.com" {
                    XCTAssertEqual(payload["AppId"] as? String,profile.clientID);XCTAssertEqual(payload["DeviceToken"] as? String,"device");XCTAssertEqual(payload["ProofKey"] as? [String:String],proof)
                    if scenario=="title-refused" { LoginProtocol.responseCode=403;return Data("synthetic-access never log".utf8) }
                    var result:[String:Any]=["UserToken":["Token":"user"]]
                    if scenario != "missing-title" { result["TitleToken"]=["Token":scenario=="empty-title" ? "":"title"] }
                    return try JSON.data(result)
                }
                XCTAssertEqual(host,"xsts.auth.xboxlive.com");XCTAssertEqual(payload["RelyingParty"] as? String,"http://xboxlive.com")
                let properties=try XCTUnwrap(payload["Properties"] as? [String:Any]);XCTAssertEqual(properties["TitleToken"] as? String,"title");XCTAssertEqual(properties["UserTokens"] as? [String],["user"]);XCTAssertEqual(properties["DeviceToken"] as? String,"device");XCTAssertEqual(properties["ProofKey"] as? [String:String],proof)
                if scenario=="refused" { LoginProtocol.responseCode=403;return Data("synthetic-access never log".utf8) }
                return try JSON.data(["Token":"presence","NotAfter":scenario=="expired" ? "2000-01-01T00:00:00Z":"2099-01-01T00:00:00Z","DisplayClaims":["xui":[["uhs":"123","xid":"42"]]]])
            }
            do {
                let result=try await BrowserLogin(session:session,profile:profile).acquirePresenceAuthorization(before) { value in
                    XCTAssertEqual(value.refreshToken,"next");XCTAssertEqual(value.authorization,before.authorization);XCTAssertEqual(value.eventToken,before.eventToken);rotated=true
                }
                XCTAssertEqual(scenario,"success");XCTAssertEqual(result.presenceAuthorization,"XBL3.0 x=123;presence")
                XCTAssertEqual(result.authorization,before.authorization);XCTAssertEqual(result.eventToken,before.eventToken);XCTAssertFalse(result.needsPresenceAuthorization());XCTAssertEqual(result.presenceSigningKey?.count,32)
                XCTAssertEqual(result.presenceAuthorizationFlow,"title-device-v1")
                let pub=try P256.Signing.PrivateKey(rawRepresentation:XCTUnwrap(result.presenceSigningKey)).publicKey.x963Representation
                let x=pub.subdata(in:1..<33).base64EncodedString().replacingOccurrences(of:"+",with:"-").replacingOccurrences(of:"/",with:"_").replacingOccurrences(of:"=",with:"")
                XCTAssertEqual(x,proof["x"])
            } catch { XCTAssertNotEqual(scenario,"success");XCTAssertFalse(error.localizedDescription.contains("synthetic-access")) }
            if scenario.contains("title") { XCTAssertFalse(endpoints.contains("xsts.auth.xboxlive.com")) }
            XCTAssertNil(before.presenceAuthorization);XCTAssertFalse(endpoints.contains { $0.contains("events") || $0.contains("presence-heartbeat") || $0.contains("achievements") })
        }
        }
    }
    func testOldAccountsDecodeAndRequireNewPresenceAuthorization() throws {
        let old=Data(#"{"authorization":"XBL3.0 x=1;old","xuid":"42","gamertag":"Synthetic","eventToken":""}"#.utf8)
        var account=try JSONDecoder().decode(Account.self,from:old);XCTAssertTrue(account.needsPresenceAuthorization())
        account.presenceAuthorization="XBL3.0 x=1;presence";account.presenceSigningKey=P256.Signing.PrivateKey().rawRepresentation;account.presenceAuthorizationExpiresAt=Date().addingTimeInterval(299);XCTAssertTrue(account.needsPresenceAuthorization())
        account.presenceAuthorizationExpiresAt=Date().addingTimeInterval(3600);XCTAssertTrue(account.needsPresenceAuthorization())
        account.presenceAuthorizationFlow="title-device-v1";XCTAssertFalse(account.needsPresenceAuthorization())
    }
    func testHeartbeatAndStopUsePresenceCredentialWithoutEventToken() async throws {
        let config=URLSessionConfiguration.ephemeral;config.protocolClasses=[LoginProtocol.self]
        let session=URLSession(configuration:config);defer { session.invalidateAndCancel();LoginProtocol.handler=nil;LoginProtocol.responseCode=200 }
        var account=Account(authorization:"XBL3.0 x=123;library",xuid:"42",gamertag:"Synthetic")
        account.presenceAuthorization="XBL3.0 x=123;presence";account.presenceSigningKey=P256.Signing.PrivateKey().rawRepresentation
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
