import XCTest
import CryptoKit
@testable import LabsMobileCore

final class SisuPreauthenticationTests: XCTestCase {
    private func body(_ request:URLRequest)->Data {
        if let b=request.httpBody { return b }
        guard let stream=request.httpBodyStream else { return Data() };stream.open();defer { stream.close() }
        var out=Data(),buffer=[UInt8](repeating:0,count:4096)
        while stream.hasBytesAvailable { let n=stream.read(&buffer,maxLength:buffer.count);if n<=0 { break };out.append(contentsOf:buffer.prefix(n)) };return out
    }
    private func unbase(_ value:String)->Data {
        let s=value.replacingOccurrences(of:"-",with:"+").replacingOccurrences(of:"_",with:"/")
        return Data(base64Encoded:s+String(repeating:"=",count:(4-s.count%4)%4))!
    }
    func testOriginalProofSurvivesPreauthBrowserSignInAndRenewal() async throws {
        let config=URLSessionConfiguration.ephemeral;config.protocolClasses=[LoginProtocol.self]
        let session=URLSession(configuration:config);defer { session.invalidateAndCancel();LoginProtocol.handler=nil;LoginProtocol.responseCode=200 }
        var calls=[String](),publicBytes=Data(),state="",rotated=false,renewing=false
        LoginProtocol.handler={ request in
            let url=request.url!,host=url.host!,bytes=self.body(request);calls.append(host+url.path);LoginProtocol.responseCode=200
            if host=="login.live.com" {
                let fields=URLComponents(string:"https://example.invalid/?"+String(decoding:bytes,as:UTF8.self))!.queryItems!
                if renewing { XCTAssertTrue(fields.contains { $0.name=="refresh_token" && $0.value=="refresh" }) }
                return try JSON.data(["access_token":"synthetic-access","refresh_token":renewing ? "rotated":"refresh"])
            }
            if host=="profile.xboxlive.com" { return try JSON.data(["profileUsers":[["id":"42","settings":[["id":"Gamertag","value":"Synthetic"]]]]]) }
            let object=try JSON.object(bytes)
            if host=="device.auth.xboxlive.com" {
                XCTAssertFalse(renewing)
                let properties=object["Properties"] as! [String:Any],proof=properties["ProofKey"] as! [String:String]
                XCTAssertEqual(properties["DeviceType"] as? String,"iOS")
                publicBytes=Data([4]);publicBytes.append(self.unbase(proof["x"]!));publicBytes.append(self.unbase(proof["y"]!))
            }
            let header=try XCTUnwrap(Data(base64Encoded:XCTUnwrap(request.value(forHTTPHeaderField:"Signature"))))
            var signed=Data(header.prefix(4));signed.append(0);signed.append(header.subdata(in:4..<12));signed.append(0);signed.append(Data(("POST\0"+url.path+"\0\0").utf8));signed.append(bytes);signed.append(0)
            let pub=try P256.Signing.PublicKey(x963Representation:publicBytes)
            XCTAssertTrue(pub.isValidSignature(try P256.Signing.ECDSASignature(rawRepresentation:header.suffix(64)),for:signed))
            if host=="device.auth.xboxlive.com" { return try JSON.data(["Token":"device","NotAfter":"2099-01-01T00:00:00Z"]) }
            XCTAssertEqual(host,"sisu.xboxlive.com");XCTAssertEqual(object["DeviceToken"] as? String,"device")
            if url.path=="/authenticate" {
                XCTAssertFalse(renewing);XCTAssertEqual(object["AppId"] as? String,LoginProfile.ios.clientID)
                let q=object["Query"] as! [String:String];state=q["state"]!
                var c=URLComponents(string:"https://login.live.com/oauth20_authorize.srf")!
                var items=q;items["client_id"]=object["AppId"] as? String;items["redirect_uri"]=object["RedirectUri"] as? String;items["response_type"]="code";items["scope"]="service::user.auth.xboxlive.com::MBI_SSL"
                c.queryItems=items.map { URLQueryItem(name:$0.key,value:$0.value) }
                return try JSON.data(["MsaOauthRedirect":c.url!.absoluteString])
            }
            XCTAssertEqual(url.path,"/authorize");if renewing { XCTAssertTrue(rotated) }
            let proof=object["ProofKey"] as! [String:String];XCTAssertEqual(self.unbase(proof["x"]!),publicBytes.subdata(in:1..<33))
            return try JSON.data(["UserToken":["Token":"user"],"TitleToken":["Token":"title"],"AuthorizationToken":["Token":renewing ? "new-session":"session","NotAfter":"2099-01-01T00:00:00Z","DisplayClaims":["xui":[["uhs":"123","xid":"42"]]]]])
        }
        let login=BrowserLogin(session:session,profile:.ios)
        let url=try await login.prepareSisuStartURL();XCTAssertEqual(url.host,"login.live.com")
        XCTAssertEqual(calls.count,2)
        var account=try await login.complete("https://login.live.com/oauth20_desktop.srf?state="+state+"&code=synthetic")
        XCTAssertEqual(calls.count,5);XCTAssertEqual(account.presenceAuthorization,account.authorization);XCTAssertEqual(account.presenceAuthorizationFlow,"sisu-preauth-v1");XCTAssertFalse(account.needsPresenceAuthorization());XCTAssertEqual(account.presenceDeviceToken,"device")
        let original=try XCTUnwrap(account.presenceSigningKey)
        XCTAssertEqual(try P256.Signing.PrivateKey(rawRepresentation:original).publicKey.x963Representation,publicBytes)
        account.eventToken="x:XBL3.0 x=123;keep-events";account.eventTokenExpiresAt=Date().addingTimeInterval(3600);account.eventTokenExpiryHash=EventTokenLifetime.fingerprint(account.eventToken)
        renewing=true
        let next=try await BrowserLogin(session:session,profile:.ios).refresh(account) { value in XCTAssertEqual(value.refreshToken,"rotated");rotated=true }
        XCTAssertEqual(calls.count,8);XCTAssertEqual(next.presenceSigningKey,original);XCTAssertEqual(next.presenceDeviceToken,account.presenceDeviceToken);XCTAssertEqual(next.authorization,"XBL3.0 x=123;new-session");XCTAssertEqual(next.presenceAuthorization,next.authorization);XCTAssertEqual(next.eventToken,account.eventToken);XCTAssertEqual(next.eventTokenExpiryHash,account.eventTokenExpiryHash)
        XCTAssertFalse(calls.contains { $0.contains("presence-heartbeat") || $0.contains("events") || $0.contains("xsts") })
    }
    func testPreauthLoginURLBindsHostStateClientRedirectAndProof() throws {
        var c=URLComponents(string:"https://login.live.com/oauth20_authorize.srf")!
        let expected=["client_id":LoginProfile.ios.clientID,"redirect_uri":"https://login.live.com/oauth20_desktop.srf","state":"state","code_challenge":"challenge","code_challenge_method":"S256","response_type":"code","scope":"service::user.auth.xboxlive.com::MBI_SSL"]
        func check(_ value:String)throws { _=try BrowserLogin.validateSisuStartURL(value,client:LoginProfile.ios.clientID,state:"state",challenge:"challenge") }
        c.queryItems=expected.map { URLQueryItem(name:$0.key,value:$0.value) };try check(c.url!.absoluteString)
        for key in expected.keys {
            var bad=expected;bad[key]="wrong";c.queryItems=bad.map { URLQueryItem(name:$0.key,value:$0.value) };XCTAssertThrowsError(try check(c.url!.absoluteString))
        }
        c.queryItems=expected.map { URLQueryItem(name:$0.key,value:$0.value) };c.queryItems!.append(URLQueryItem(name:"client_id",value:LoginProfile.ios.clientID));XCTAssertThrowsError(try check(c.url!.absoluteString))
        c.queryItems=expected.map { URLQueryItem(name:$0.key,value:$0.value) };c.host="example.invalid";XCTAssertThrowsError(try check(c.url!.absoluteString))
    }
    func testExpiredDeviceStopsRenewalWithoutNetworkOrRotation() async throws {
        let config=URLSessionConfiguration.ephemeral;config.protocolClasses=[LoginProtocol.self]
        let session=URLSession(configuration:config);defer { session.invalidateAndCancel();LoginProtocol.handler=nil }
        var calls=0;LoginProtocol.handler={ _ in calls+=1;return Data() }
        var account=Account(authorization:"XBL3.0 x=1;saved",xuid:"42",gamertag:"Synthetic",eventToken:"x:XBL3.0 x=1;events")
        account.loginProfile=LoginProfile.ios.rawValue;account.refreshToken="refresh";account.presenceAuthorizationFlow="sisu-preauth-v1";account.presenceSigningKey=P256.Signing.PrivateKey().rawRepresentation;account.presenceDeviceToken="device";account.presenceDeviceExpiresAt=Date().addingTimeInterval(-1)
        do { _=try await BrowserLogin(session:session,profile:.ios).refresh(account) { _ in XCTFail("Must not rotate expired device") };XCTFail("Must require a new explicit sign-in") } catch { }
        XCTAssertEqual(calls,0);XCTAssertEqual(account.eventToken,"x:XBL3.0 x=1;events")
    }
    func testXboxErrorHeaderIsSeparateAndStrictlySanitized() throws {
        let url=URL(string:"https://presence-heartbeat.xboxlive.com/")!
        for value in ["2148916233","0x8015DC00","XBL3.0 x=secret;opaque","secret-token"] {
            let response=HTTPURLResponse(url:url,statusCode:403,httpVersion:nil,headerFields:["x-err":value])!
            let error=XboxHTTPFailure(status:403,endpoint:url.host!,message:"Refused",response:response,data:try JSON.data(["code":"Forbidden"]))
            XCTAssertEqual(error.serverCode,"Forbidden")
            if value.hasPrefix("214") || value.hasPrefix("0x") { XCTAssertEqual(error.xboxErrorCode,value);XCTAssertTrue(error.diagnostic.contains(value)) }
            else { XCTAssertNil(error.xboxErrorCode);XCTAssertFalse(error.diagnostic.contains("secret")) }
        }
    }
}
