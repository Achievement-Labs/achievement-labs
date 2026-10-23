import XCTest
import CryptoKit
@testable import LabsMobileCore

final class LoginProtocol: URLProtocol {
    static var responseCode = 200
    static var handler: ((URLRequest) throws -> Data)?
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            let data = try Self.handler!(request)
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url:request.url!, statusCode:Self.responseCode, httpVersion:nil, headerFields:nil)!, cacheStoragePolicy:.notAllowed)
            client?.urlProtocol(self, didLoad:data); client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError:error) }
    }
    override func stopLoading() {}
}
final class BrowserLoginTests: XCTestCase {
    func testSyntheticOAuthAndSignedXboxExchange() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [LoginProtocol.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel(); LoginProtocol.handler = nil }
        let login = BrowserLogin(session:session)
        let query = URLComponents(url:login.startURL,resolvingAgainstBaseURL:false)!.queryItems!
        let state = query.first { $0.name == "state" }!.value!
        let challenge = query.first { $0.name == "code_challenge" }!.value!
        var stages = [String]()
        func unbase(_ value: String) -> Data {
            let s = value.replacingOccurrences(of:"-",with:"+").replacingOccurrences(of:"_",with:"/")
            return Data(base64Encoded:s + String(repeating:"=",count:(4-s.count%4)%4))!
        }
        LoginProtocol.handler = { request in
            let url = request.url!; stages.append(url.host! + url.path)
            var body = request.httpBody ?? Data()
            if let stream = request.httpBodyStream {
                stream.open(); defer { stream.close() }; var buffer = [UInt8](repeating:0,count:4096)
                while stream.hasBytesAvailable { let n = stream.read(&buffer,maxLength:buffer.count); if n <= 0 { break }; body.append(contentsOf:buffer.prefix(n)) }
            }
            if url.host == "login.live.com" {
                let fields = URLComponents(string:"https://example.invalid/?" + String(decoding:body,as:UTF8.self))!.queryItems!
                let verifier = fields.first { $0.name == "code_verifier" }!.value!
                XCTAssertEqual(Data(SHA256.hash(data:Data(verifier.utf8))),unbase(challenge))
                XCTAssertEqual(fields.first { $0.name == "code" }!.value,"synthetic-code")
                return try JSON.data(["access_token":"synthetic-access","refresh_token":"synthetic-refresh"])
            }
            if url.host == "profile.xboxlive.com" {
                XCTAssertEqual(request.value(forHTTPHeaderField:"Authorization"),"XBL3.0 x=123;synthetic-xbox")
                return try JSON.data(["profileUsers":[["id":"42","settings":[["id":"Gamertag","value":"Synthetic account"]]]]])
            }
            let object = try JSON.object(body)
            let proof = (object["ProofKey"] ?? (object["Properties"] as? [String:Any])?["ProofKey"]) as! [String:String]
            var publicBytes = Data([4]); publicBytes.append(unbase(proof["x"]!)); publicBytes.append(unbase(proof["y"]!))
            let key = try P256.Signing.PublicKey(x963Representation:publicBytes)
            let header = Data(base64Encoded:request.value(forHTTPHeaderField:"Signature")!)!
            XCTAssertEqual(header.count,76)
            var signed = Data(header.prefix(4)); signed.append(0); signed.append(header.subdata(in:4..<12)); signed.append(0)
            signed.append(Data(("POST\0" + url.path + "\0\0").utf8)); signed.append(body); signed.append(0)
            XCTAssertTrue(key.isValidSignature(try P256.Signing.ECDSASignature(rawRepresentation:header.suffix(64)),for:signed))
            if url.host == "device.auth.xboxlive.com" { return try JSON.data(["Token":"synthetic-device"]) }
            XCTAssertEqual(object["AccessToken"] as? String,"t=synthetic-access")
            XCTAssertEqual(object["DeviceToken"] as? String,"synthetic-device")
            XCTAssertEqual(object["RelyingParty"] as? String,"http://xboxlive.com")
            return try JSON.data(["AuthorizationToken":["Token":"synthetic-xbox","NotAfter":"2099-01-01T00:00:00Z","DisplayClaims":["xui":[["uhs":"123"]]]]])
        }
        let account = try await login.complete("https://login.live.com/oauth20_desktop.srf?state=\(state)&code=synthetic-code")
        XCTAssertEqual(account.refreshToken,"synthetic-refresh"); XCTAssertNotNil(account.authorizationExpiresAt); XCTAssertFalse(account.needsRefresh()); XCTAssertEqual(account.xuid,"42"); XCTAssertEqual(account.gamertag,"Synthetic account")
        XCTAssertEqual(stages.count,4)
        do { _ = try await login.complete("https://login.live.com/oauth20_desktop.srf?state=\(state)&code=synthetic-code"); XCTFail("Callback must be single-use") } catch { }
        XCTAssertEqual(stages.count,4)
    }
}
