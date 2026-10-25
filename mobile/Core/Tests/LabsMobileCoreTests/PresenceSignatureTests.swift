import XCTest
import CryptoKit
@testable import LabsMobileCore
final class PresenceSignatureTests:XCTestCase {
    func testSignedPostAndDeleteBindMethodPathAuthorizationAndExactBody() throws {
        let key=P256.Signing.PrivateKey(),url=URL(string:"https://presence-heartbeat.xboxlive.com/users/xuid(42)/devices/current/")!,at=Date(timeIntervalSince1970:1700000000)
        for method in ["POST","DELETE"] {
            let body=method == "POST" ? Data(#"{"titles":[{"id":"7"}]}"#.utf8):Data()
            let bytes=try XCTUnwrap(Data(base64Encoded:PresenceSignature.header(method:method,url:url,authorization:"XBL3.0 x=1;synthetic",body:body,key:key.rawRepresentation,at:at)))
            XCTAssertEqual(bytes.count,76);XCTAssertEqual(Array(bytes.prefix(4)),[0,0,0,1])
            var message=Data(bytes.prefix(4));message.append(0);message.append(bytes.subdata(in:4..<12));message.append(0)
            message.append(Data((method+"\0"+url.path+"\0XBL3.0 x=1;synthetic\0").utf8));message.append(body);message.append(0)
            let signature=try P256.Signing.ECDSASignature(rawRepresentation:bytes.suffix(64))
            XCTAssertTrue(key.publicKey.isValidSignature(signature,for:message))
            message[message.count-1]=1;XCTAssertFalse(key.publicKey.isValidSignature(signature,for:message))
        }
    }
    func testProofCannotBeSentToAnotherHost() throws {
        XCTAssertThrowsError(try PresenceSignature.header(method:"POST",url:URL(string:"https://example.com/")!,authorization:"synthetic",body:Data(),key:P256.Signing.PrivateKey().rawRepresentation))
    }
    func testIssuedExpiryNeverFollowsAReplacementOrImportedToken() {
        var a=Account(authorization:"synthetic",xuid:"42",gamertag:"Synthetic",eventToken:"token-one")
        let expiry=Date(timeIntervalSince1970:1700000000);a.eventTokenExpiresAt=expiry;a.eventTokenExpiryHash=EventTokenLifetime.fingerprint(a.eventToken)
        XCTAssertEqual(EventTokenLifetime.expiry(a),expiry)
        a.eventToken="token-two";XCTAssertNil(EventTokenLifetime.expiry(a))
        a.eventToken="";XCTAssertNil(EventTokenLifetime.expiry(a))
    }
}
