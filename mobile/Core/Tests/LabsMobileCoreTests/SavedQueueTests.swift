import XCTest
@testable import LabsMobileCore

final class SavedQueueTests: XCTestCase {
    private func item(_ title: String, _ achievement: String) throws -> QueueItem {
        QueueItem(game:try Game(["titleId":title,"name":"Game " + title]),achievement:try Achievement(["id":achievement,"name":"Achievement " + achievement]),delay:120)
    }
    func testSeparateSaveDoesNotChangeCurrentQueueAndSurvivesRestart() throws {
        var book = SavedQueueBook(); let first = try item("7","1"), second = try item("8","2")
        book.capture([first]); book.saveSeparate([second])
        XCTAssertEqual(book.current.map(\.id),["7/1"])
        let restored = try JSONDecoder().decode(SavedQueueBook.self,from:JSONEncoder().encode(book))
        XCTAssertEqual(restored.games["7"]?.items.count,1); XCTAssertEqual(restored.games["8"]?.items.count,1)
        XCTAssertEqual(restored.current.first?.delay,120); try restored.validate()
    }
    func testDeletedGameDoesNotReturnAfterAutosaveOrRestart() throws {
        var book = SavedQueueBook(); let a = try item("7","1"), b = try item("8","1")
        book.capture([a,b]); book.deleteGame("7")
        XCTAssertNil(book.games["7"]); XCTAssertEqual(book.current.map(\.id),["8/1"])
        book.capture(book.current)
        var restarted = try JSONDecoder().decode(SavedQueueBook.self,from:JSONEncoder().encode(book))
        restarted.capture(restarted.current); try restarted.validate()
        XCTAssertNil(restarted.games["7"]); XCTAssertNotNil(restarted.games["8"])
        restarted.deleteGame("8"); restarted.capture(restarted.current)
        XCTAssertTrue(restarted.current.isEmpty); XCTAssertTrue(restarted.games.isEmpty)
    }
    func testRemovalUpdatesOnlyTheAffectedSavedGame() throws {
        var book = SavedQueueBook(); let a = try item("7","1"), b = try item("7","2"), other = try item("8","1")
        book.capture([a,b]); book.saveSeparate([other]); book.capture([b])
        XCTAssertEqual(book.games["7"]?.items.map(\.id),["7/2"])
        XCTAssertNotNil(book.games["8"])
        book.capture([]); XCTAssertNotNil(book.games["7"]); XCTAssertNotNil(book.games["8"])
    }
    func testSwitchingActiveGamePreservesPreviousSave() throws {
        var book = SavedQueueBook(); let a = try item("7","1"), b = try item("8","1")
        book.capture([a]); book.saveSeparate([b]); book.current = []; book.capture([b])
        XCTAssertNotNil(book.games["7"]); XCTAssertEqual(book.current.first?.game.id,"8")
    }
    func testRemainingDelayAndLegacyQueueDecode() throws {
        var a = try item("7","1"); a.remainingDelay = 23
        var book = SavedQueueBook(); book.capture([a])
        let restored = try JSONDecoder().decode(SavedQueueBook.self,from:JSONEncoder().encode(book))
        XCTAssertEqual(restored.current.first?.remainingDelay,23)
        var json = try JSONSerialization.jsonObject(with:JSONEncoder().encode(a)) as! [String:Any]; json.removeValue(forKey:"remainingDelay")
        XCTAssertNil(try JSONDecoder().decode(QueueItem.self,from:JSON.data(json)).remainingDelay)
        a.remainingDelay = -1; book.capture([a]); XCTAssertThrowsError(try book.validate())
    }
    func testDuplicateAchievementAndWrongTitleAreRejected() throws {
        let a = try item("7","1"); var book = SavedQueueBook(); book.capture([a,a])
        XCTAssertThrowsError(try book.validate())
        book = SavedQueueBook(); book.games["8"] = SavedGameQueue(items:[a]); XCTAssertThrowsError(try book.validate())
    }
}
