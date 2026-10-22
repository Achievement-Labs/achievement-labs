import Foundation

public struct MappingStatus: Codable {
    public var count: Int; public var classification: String; public var reason: String?
}
public struct Replacement: Codable { public let target: String; public let value: String }
public struct MobileRecipes: Codable { public let recipes: [String: [[Replacement]]] }
public struct PackDocument: Codable {
    public let schemaVersion: Int; public let titles: [PackTitle]
}
public struct PackTitle: Codable {
    public let titleId: String; public let platforms: [String]; public let packs: [Pack]
}
public struct Pack: Codable {
    public let name: String; public let kind: String; public let achievements: [PackAchievement]
}
public struct PackAchievement: Codable { public let id: String; public let name: String }

public final class Catalogue {
    public private(set) var index: [String: MappingStatus]
    private let bundledIDs: Set<String>
    private var synced: [String:SyncedTitle] = [:]
    private let folder: URL
    private let packs: [PackTitle]
    private let hubIDs: Set<String>
    private let totals: [TotalEntry]
    private var cachedTitle: String?
    private var cachedRecipes: MobileRecipes?
    private var cachedTemplate: String?
    public init(folder: URL) throws {
        self.folder = folder
        index = try JSONDecoder().decode([String: MappingStatus].self, from: Data(contentsOf: folder.appendingPathComponent("MobileIndex.json")))
        bundledIDs = Set(index.keys)
        hubIDs = Set(try JSONDecoder().decode(PackDocument.self, from: Data(contentsOf: folder.appendingPathComponent("achievement-hubs.json"))).titles.map(\.titleId))
        totals = (try? JSONDecoder().decode(TotalDocument.self, from: Data(contentsOf: folder.appendingPathComponent("achievement-totals.json"))).entries) ?? []
        packs = try ["achievement-packs.json", "achievement-hubs.json"].flatMap {
            try JSONDecoder().decode(PackDocument.self, from: Data(contentsOf: folder.appendingPathComponent($0))).titles
        }
    }
    public func recipes(_ title: String) throws -> MobileRecipes {
        guard JSON.numeric(title), let status = index[title], status.reason == nil else { throw LabsError.message(index[title]?.reason ?? "No mobile event data is available for this title.") }
        if let remote = synced[title] { return remote.recipes }
        if cachedTitle == title, let cachedRecipes { return cachedRecipes }
        let value = try JSONDecoder().decode(MobileRecipes.self, from: Data(contentsOf: folder.appendingPathComponent("Events/\(title).mapping.json")))
        cachedTitle = title; cachedRecipes = value; cachedTemplate = nil
        return value
    }
    public func payloads(title: String, achievement: String, xuid: String) throws -> [String] {
        let mapping = try recipes(title)
        let template = try synced[title]?.template ?? cachedTemplate ?? String(contentsOf: folder.appendingPathComponent("Events/\(title).json"), encoding: .utf8)
        if synced[title] == nil { cachedTemplate = template }
        guard let requests = mapping.recipes[achievement] else { throw LabsError.message("No verified mobile mapping for this achievement.") }
        return try Self.build(template: template, requests: requests, title: title, xuid: xuid)
    }
    public static func build(template: String, requests: [[Replacement]], title: String, xuid: String, date: Date = Date()) throws -> [String] {
        guard JSON.numeric(title), JSON.numeric(xuid), !requests.isEmpty, requests.count <= 1000 else { throw LabsError.message("Invalid mapping identity or request count.") }
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let stamp = formatter.string(from: date); let session = UUID().uuidString.lowercased()
        return try requests.enumerated().map { n, operations in
            var body = template
            guard !operations.isEmpty else { throw LabsError.message("Empty mapping.") }
            for op in operations {
                guard body.utf8.count <= 2*1024*1024, op.value.utf8.count <= 1024*1024 else { throw LabsError.message("Event payload exceeds its size limit.") }
                guard !op.target.isEmpty, body.contains(op.target) else { throw LabsError.message("Mapping target does not match the template.") }
                let occurrences = body.components(separatedBy:op.target).count - 1
                let estimate = body.utf8.count + occurrences * (op.value.utf8.count - op.target.utf8.count)
                guard estimate <= 2*1024*1024 else { throw LabsError.message("Event payload expansion limit exceeded.") }
                body = body.replacingOccurrences(of: op.target, with: op.value)
            }
            for (target, value) in ["REPLACESEQ":String(Int64(date.timeIntervalSince1970 * 1000) + Int64(n)), "REPLACETIME":stamp, "REPLACEXUID":xuid, "REPLACESESSIONGUID":session] { body = body.replacingOccurrences(of: target, with: value) }
            guard body.range(of: "REPLACE[A-Z0-9_]+", options: .regularExpression) == nil else { throw LabsError.message("Unresolved event placeholder.") }
            let j = try JSON.object(Data(body.utf8)); let data = j["data"] as? [String: Any] ?? [:]; let base = data["baseData"] as? [String: Any] ?? [:]
            guard JSON.string(base["titleId"]) == title, UUID(uuidString: JSON.string(base["serviceConfigId"])) != nil else { throw LabsError.message("Event title or service identity does not match.") }
            // OneCollector consumes newline-delimited records. Match desktop framing.
            return String(decoding: try JSON.data(j), as: UTF8.self)
        }
    }
    public func merge(_ feed: EventFeed, cache: URL) throws -> Int {
        guard feed.schemaVersion == 1, feed.archiveSHA256.range(of:#"^[a-f0-9]{64}$"#,options:.regularExpression) != nil,
              let date = ServiceDates.parse(feed.generatedAt), date < Date().addingTimeInterval(86400), feed.titles.count <= 5000 else { throw LabsError.message("Unsupported event feed. Bundled data retained.") }
        var merged = synced; var count = 0; var recipeCount = 0
        for (title,value) in feed.titles where !bundledIDs.contains(title) && merged[title] == nil {
            guard JSON.numeric(title), title.count <= 10, value.template.utf8.count <= 1024*1024, value.recipes.recipes.count <= 10000 else { throw LabsError.message("Event feed limit exceeded.") }
            for (id,requests) in value.recipes.recipes {
                recipeCount += 1
                guard JSON.numeric(id), recipeCount <= 50000, !requests.isEmpty, requests.count <= 100, requests.allSatisfy({ !$0.isEmpty && $0.count <= 100 }) else { throw LabsError.message("Invalid synced recipe.") }
                _ = try Self.build(template:value.template,requests:requests,title:title,xuid:"0")
            }
            guard !value.recipes.recipes.isEmpty else { continue }; merged[title] = value; count += 1
        }
        var saved = feed; saved.titles = merged
        let data = try JSONEncoder().encode(saved)
        guard data.count <= 32*1024*1024 else { throw LabsError.message("Event cache limit exceeded.") }
        try data.write(to:cache,options:.atomic)
        synced = merged
        for (title,value) in synced { index[title] = MappingStatus(count:value.recipes.recipes.count,classification:"Testing · synced",reason:nil) }
        cachedTitle = nil; cachedRecipes = nil; cachedTemplate = nil
        return count
    }
    public func loadCache(_ cache: URL) throws {
        let values = try cache.resourceValues(forKeys:[.fileSizeKey]); guard values.fileSize ?? 0 <= 32*1024*1024 else { throw LabsError.message("Event cache is too large.") }
        let data = try Data(contentsOf:cache); _ = try merge(JSONDecoder().decode(EventFeed.self,from:data),cache:cache)
    }
    private func definition(_ game: Game) -> PackTitle? { packs.first { $0.titleId == game.id && !$0.platforms.filter(game.platforms.contains).isEmpty } }
    public static func normalized(_ name: String) -> String { name.lowercased().unicodeScalars.filter(CharacterSet.alphanumerics.contains).map(String.init).joined() }
    public func group(_ achievement: Achievement, game: Game) -> String {
        guard let title = definition(game) else { return "Achievements" }
        return title.packs.first { $0.achievements.contains { $0.id == achievement.id && Self.normalized($0.name) == Self.normalized(achievement.name) } }?.name ?? "Unclassified"
    }
    public func groups(_ game: Game) -> [String] { (definition(game)?.packs.map(\.name) ?? ["Achievements"]) + ["Unclassified"] }
    public func isHub(_ game: Game) -> Bool { hubIDs.contains(game.id) }
    public func applyingTotal(_ input: Game) -> Game {
        var game = input
        if game.total <= 0, let entry = totals.first(where: { $0.titleId == game.id && !$0.platforms.filter(game.platforms.contains).isEmpty }), entry.total >= game.unlocked { game.total = entry.total }
        return game
    }
    public func baseComplete(_ rows: [Achievement], game: Game) -> Bool {
        guard !rows.isEmpty, !isHub(game) else { return false }
        if rows.allSatisfy(\.unlocked) { return true }
        guard let base = definition(game)?.packs.first(where: { $0.kind == "base" }), !base.achievements.isEmpty else { return false }
        return base.achievements.allSatisfy { item in rows.contains { $0.id == item.id && Self.normalized($0.name) == Self.normalized(item.name) && $0.unlocked } }
    }
}

private struct TotalDocument: Decodable { let entries: [TotalEntry] }
private struct TotalEntry: Decodable { let titleId: String; let platforms: [String]; let total: Int }

