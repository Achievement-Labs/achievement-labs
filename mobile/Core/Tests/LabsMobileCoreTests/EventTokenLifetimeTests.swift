import XCTest
@testable import LabsMobileCore
final class EventTokenLifetimeTests:XCTestCase {
    func testIssuedExpiryNeverFollowsAReplacementOrImportedToken() {
        var a=Account(authorization:"synthetic",xuid:"42",gamertag:"Synthetic",eventToken:"token-one")
        let expiry=Date(timeIntervalSince1970:1700000000);a.eventTokenExpiresAt=expiry;a.eventTokenExpiryHash=EventTokenLifetime.fingerprint(a.eventToken)
        XCTAssertEqual(EventTokenLifetime.expiry(a),expiry)
        a.eventToken="token-two";XCTAssertNil(EventTokenLifetime.expiry(a))
        a.eventToken="";XCTAssertNil(EventTokenLifetime.expiry(a))
    }
    func testObsoletePresenceDataIsIgnoredWhenAnAccountIsSaved() throws {
        let json=Data(#"{"authorization":"existing","xuid":"42","gamertag":"Synthetic","eventToken":"event","refreshToken":"renewal","presenceAuthorization":"obsolete","presenceSigningKey":"obsolete"}"#.utf8)
        let account=try JSONDecoder().decode(Account.self,from:json)
        let saved=try JSONSerialization.jsonObject(with:JSONEncoder().encode(account)) as! [String:Any]
        XCTAssertEqual(account.authorization,"existing"); XCTAssertEqual(account.eventToken,"event"); XCTAssertEqual(account.refreshToken,"renewal")
        XCTAssertNil(saved["presenceAuthorization"]); XCTAssertNil(saved["presenceSigningKey"])
    }
}
