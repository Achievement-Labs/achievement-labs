import XCTest
@testable import LabsMobileCore

final class CoreTests: XCTestCase {
    func testBrowserCallbackValidation() throws {
        let login = BrowserLogin()
        let query = URLComponents(url: login.startURL, resolvingAgainstBaseURL: false)!.queryItems!
        let state = query.first { $0.name == "state" }!.value!
        let callback = "https://login.live.com/oauth20_desktop.srf?state=\(state)&code=synthetic"
        XCTAssertEqual(try BrowserLogin.validateRedirect(callback, state: state), "synthetic")
        for value in [callback + "&code=other", callback + "&state=other", callback + "&error=denied", callback.replacingOccurrences(of: "https:", with: "http:"), callback.replacingOccurrences(of: "login.live.com", with: "example.com"), callback + "#fragment"] {
            XCTAssertThrowsError(try BrowserLogin.validateRedirect(value, state: state))
        }
        XCTAssertThrowsError(try BrowserLogin.validateRedirect(callback, state: "different"))
    }

    func testTokenNormalizationAndExpiry() throws {
        XCTAssertEqual(try Tokens.normalize("tickets: \"1\"=\"x:XBL3.0 x=synthetic;opaque\"", event: true), "x:XBL3.0 x=synthetic;opaque")
        XCTAssertEqual(try Tokens.normalize("\"XBL3.0 x=abc-123;opaque\"", event: false), "XBL3.0 x=abc-123;opaque")
        XCTAssertThrowsError(try Tokens.normalize("XBL3.0 x=1;secret\r\nOther: value", event: true))
        XCTAssertThrowsError(try Tokens.normalize("not a token", event: true))
        let expired = Data("{\"exp\":1}".utf8).base64EncodedString().replacingOccurrences(of:"=",with:"")
        XCTAssertThrowsError(try Tokens.normalize("x:XBL3.0 x=123;e30.\(expired).sig", event: true))
        XCTAssertNoThrow(try Tokens.normalize("x:XBL3.0 x=123;a.b.c.d.e", event: true))
    }
    func testCollectorRejection() throws {
        for response in [#"{"acc":0}"#, #"{"acc":1,"rej":1}"#, #"{"efi":{"0":"all"}}"#, #"{"TokenCrackingFailure":true}"#, #"{"efi":{"0":[1]}}"#] {
            XCTAssertThrowsError(try XboxService.validateCollector(Data(response.utf8)))
        }
        XCTAssertNoThrow(try XboxService.validateCollector(Data(#"{"acc":1,"rej":0,"TokenCrackingFailure":false}"#.utf8)))
    }
    func testPayloadIdentityAndMissingTargets() throws {
        let template = #"{"name":"Unlock","data":{"baseData":{"titleId":"7","serviceConfigId":"00000000-0000-0000-0000-000000000001","xuid":"REPLACEXUID","id":REPLACEID}}}"#
        let op = Replacement(target:"REPLACEID",value:"9")
        let payload = try Catalogue.build(template:template,requests:[[op]],title:"7",xuid:"123")
        XCTAssertEqual(payload.count,1)
        XCTAssertTrue(payload[0].contains("123"))
        XCTAssertThrowsError(try Catalogue.build(template:template,requests:[[op]],title:"8",xuid:"123"))
        XCTAssertThrowsError(try Catalogue.build(template:template,requests:[[Replacement(target:"MISSING",value:"1")]],title:"7",xuid:"123"))
        XCTAssertThrowsError(try Catalogue.build(template:template,requests:[[op]],title:"7",xuid:"injected"))
    }
    func testUnknownCountsDoNotProveCompletion() throws {
        let unknown = try Game(["titleId":"7","name":"Test","achievement":["currentAchievements":0,"totalAchievements":0]])
        XCTAssertFalse(unknown.complete)
        let complete = try Game(["titleId":"7","achievement":["currentAchievements":2,"totalAchievements":2]])
        XCTAssertTrue(complete.complete)
    }
    func testLegacyParsingAndEventRequirement() throws {
        let legacy = try Achievement(["id":7,"name":"Test","unlocked":true,"gamerscore":20],legacy:true)
        XCTAssertTrue(legacy.unlocked); XCTAssertEqual(legacy.score,"20")
        let modern = try Achievement(["id":"7","name":"Test","progression":["requirements":[["id":"00000000-0000-0000-0000-000000000001"]]]])
        XCTAssertTrue(modern.eventBased)
    }
    func testGeneratedCatalogue() throws {
        guard let path = ProcessInfo.processInfo.environment["LABS_CATALOGUE"] else { throw XCTSkip("Run prepare_catalogue.py and set LABS_CATALOGUE for catalogue integration checks.") }
        let c = try Catalogue(folder:URL(fileURLWithPath:path))
        let report = try JSON.object(Data(contentsOf:URL(fileURLWithPath:path).appendingPathComponent("catalogue-report.json")))
        XCTAssertEqual(c.index.count,(report["sourceTitleBlocks"] as? Int) ?? -1)
        XCTAssertGreaterThan(c.index.count,400)
        for title in ["345903534","796798669","125965001","2154930","1766089184","515050978"] { XCTAssertNotNil(c.index[title]?.reason); XCTAssertThrowsError(try c.recipes(title)) }
        XCTAssertNotNil(c.index["1100121502"])
        var count = 0
        for (title,status) in c.index where status.reason == nil {
            let recipes = try c.recipes(title)
            for id in recipes.recipes.keys {
                let bodies = try c.payloads(title:title,achievement:id,xuid:"0")
                XCTAssertFalse(bodies.isEmpty)
                for body in bodies { XCTAssertFalse(body.contains("\n")); XCTAssertFalse(body.contains("\r")) }
                count += 1
            }
        }
        XCTAssertGreaterThan(count,16000)
    }
}

