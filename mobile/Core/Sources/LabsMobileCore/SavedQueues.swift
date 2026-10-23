import Foundation

public struct SavedGameQueue: Codable, Identifiable {
    public var id: String
    public var name: String
    public var items: [QueueItem]
    public var updated: Date
    public init(items: [QueueItem], date: Date = Date()) {
        id = items.first?.game.id ?? ""; name = items.first?.game.name ?? "Game"
        self.items = items; updated = date
    }
}

/// Stored inside an account-specific file; contains no authorization or event token.
public struct SavedQueueBook: Codable {
    public var current: [QueueItem] = []
    public var games: [String: SavedGameQueue] = [:]
    public init() {}
    public var sorted: [SavedGameQueue] { games.values.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending } }
    public mutating func capture(_ items: [QueueItem]) {
        let affected = Set(current.map { $0.game.id } + items.map { $0.game.id })
        for title in affected {
            let group = items.filter { $0.game.id == title }
            if !group.isEmpty { games[title] = SavedGameQueue(items:group) }
        }
        current = items
    }
    public mutating func saveSeparate(_ items: [QueueItem]) {
        for group in Dictionary(grouping:items,by:{ $0.game.id }).values {
            if let title = group.first?.game.id { games[title] = SavedGameQueue(items:group) }
        }
    }
    /// Remove its loaded items as well so a later autosave cannot resurrect this save.
    public mutating func deleteGame(_ id: String) {
        games.removeValue(forKey:id)
        current.removeAll { $0.game.id == id }
    }
    public func validate() throws {
        for list in [current] + games.values.map(\.items) {
            guard list.count <= 20000, Set(list.map(\.id)).count == list.count,
                  list.allSatisfy({ JSON.numeric($0.game.id) && JSON.numeric($0.achievement.id) && $0.delay.isFinite && (0...31_536_000).contains($0.delay) && ($0.remainingDelay == nil || ($0.remainingDelay!.isFinite && (0...31_536_000).contains($0.remainingDelay!))) }) else {
                throw LabsError.message("Saved queue is invalid. Existing files were retained.")
            }
        }
        for (id,game) in games { guard JSON.numeric(id), id == game.id, game.items.allSatisfy({ $0.game.id == id }) else { throw LabsError.message("Saved game queue identity does not match.") } }
    }
}
