import XCTest
@testable import LabsMobileCore

final class RefinementTests: XCTestCase {
    func testArtworkFallbackAndHTTPS() throws {
        let game = try Game(["titleId":"7","images":["BoxArt":[["uri":"//store-images.s-microsoft.com/art.png"]]]])
        XCTAssertEqual(game.image,"https://store-images.s-microsoft.com/art.png")
        XCTAssertNil(Artwork.resolve("javascript:alert(1)")); XCTAssertNil(Artwork.resolve("https://user:password@example.com/a"))
        XCTAssertEqual(Artwork.resolve("http://example.com/a"),"https://example.com/a")
    }
    func testTimingMatchesIdentityAndPreservesIntervals() throws {
        let game = try Game(["titleId":"7"])
        func row(_ id: String, _ name: String, achieved: Bool, time: String? = nil) throws -> Achievement {
            var j: [String:Any] = ["id":id,"name":name,"progressState":achieved ? "Achieved" : "NotStarted"]
            if let time { j["progression"] = ["timeUnlocked":time] }; return try Achievement(j)
        }
        let own = try [row("1","First",achieved:false),row("2","Second",achieved:false),row("3","Wrong name",achieved:false),row("4","Done",achieved:true)]
        let ref = try [row("2","Second",achieved:true,time:"2026-01-01T00:02:00Z"),row("1","First",achieved:true,time:"2026-01-01T00:00:00.000Z"),row("3","Mismatch",achieved:true,time:"2026-01-01T00:01:00Z"),row("4","Done",achieved:true,time:"2026-01-01T00:04:00Z")]
        let plan = try TimingPlan.build(game:game,own:own,reference:ref,gamertag:"Synthetic",supported:{ _ in true })
        XCTAssertEqual(plan.items.map(\.achievement.id),["1","2"]); XCTAssertEqual(plan.items.map(\.delay),[0,120]); XCTAssertEqual(plan.skipped,2)
        XCTAssertEqual(plan.items[1].referenceGamertag,"Synthetic")
        XCTAssertTrue(try TimingPlan.build(game:game,own:own,reference:ref,gamertag:"Synthetic",supported:{ _ in false }).items.isEmpty)
    }
    func testOldAccountAndQueueStorageStillDecodes() throws {
        let account = try JSONDecoder().decode(Account.self,from:Data(#"{"authorization":"synthetic","xuid":"1","gamertag":"Test","eventToken":""}"#.utf8))
        XCTAssertNil(account.gamerpic); XCTAssertNil(account.loginProfile)
        let game = try Game(["titleId":"7"]); let row = try Achievement(["id":"1"])
        let item = QueueItem(game:game,achievement:row,delay:60)
        var j = try JSON.object(JSONEncoder().encode(item)); j.removeValue(forKey:"referenceGamertag"); j.removeValue(forKey:"referenceDate")
        let decoded = try JSONDecoder().decode(QueueItem.self,from:JSON.data(j))
        XCTAssertEqual(decoded.delay,60); XCTAssertNil(decoded.referenceGamertag)
    }
    func testProfilesAreExplicitAndUseRegisteredRedirect() {
        for profile in LoginProfile.allCases {
            let login = BrowserLogin(profile:profile)
            let query = URLComponents(url:login.startURL,resolvingAgainstBaseURL:false)!.queryItems!
            XCTAssertEqual(query.first { $0.name == "client_id" }?.value,profile.clientID)
            XCTAssertEqual(query.first { $0.name == "redirect_uri" }?.value,"https://login.live.com/oauth20_desktop.srf")
        }
    }
    func testBundledTotalsDoNotOverwriteUnlockedProgress() throws {
        guard let path = ProcessInfo.processInfo.environment["LABS_CATALOGUE"] else { throw XCTSkip("Generated catalogue required") }
        let c = try Catalogue(folder:URL(fileURLWithPath:path))
        let game = try Game(["titleId":"2154930","devices":["XboxOne"],"achievement":["currentAchievements":12]])
        let result = c.applyingTotal(game); XCTAssertEqual(result.total,50); XCTAssertEqual(result.unlocked,12)
        let wrongEdition = try Game(["titleId":"2154930","devices":["PC"],"achievement":["currentAchievements":12]])
        XCTAssertEqual(c.applyingTotal(wrongEdition).total,0)
        let hub = try Game(["titleId":"2001700854","devices":["XboxOne"],"achievement":["currentAchievements":1,"totalAchievements":1]])
        XCTAssertTrue(c.isHub(hub)); XCTAssertFalse(c.baseComplete([try Achievement(["id":"1","progressState":"Achieved"])],game:hub))
    }
}
