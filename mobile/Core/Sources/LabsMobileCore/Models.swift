import Foundation

public enum LabsError: LocalizedError {
    case message(String)
    public var errorDescription: String? { if case .message(let s) = self { return s }; return nil }
}
public struct Account: Codable, Equatable {
    public var authorization: String
    public var xuid: String
    public var gamertag: String
    public var eventToken: String
    public var gamerpic: String?
    public var gamerscore: String?
    public var loginProfile: String?
    public var refreshToken: String?
    public var authorizationExpiresAt: Date?
    public var eventTokenExpiresAt: Date?
    public var eventTokenExpiryHash: String?
    public func needsRefresh(at date: Date = Date()) -> Bool { authorizationExpiresAt.map { $0 <= date.addingTimeInterval(300) } ?? false }
    public init(authorization: String, xuid: String, gamertag: String, eventToken: String = "") {
        self.authorization = authorization; self.xuid = xuid; self.gamertag = gamertag; self.eventToken = eventToken
    }
}
public struct Game: Codable, Identifiable, Hashable {
    public var id: String; public var name: String; public var platforms: [String]
    public var unlocked: Int; public var total: Int; public var image: String; public var lastPlayed: String
    public var legacy: Bool { platforms.contains { ["Xbox360", "GFWL"].contains($0) } }
    public var complete: Bool { total > 0 && unlocked == total }
    public init(_ j: [String: Any]) throws {
        id = try JSON.requiredID(j, "titleId"); name = JSON.string(j["name"]); platforms = j["devices"] as? [String] ?? []
        let a = j["achievement"] as? [String: Any] ?? [:]
        unlocked = (a["currentAchievements"] as? Int) ?? 0; total = (a["totalAchievements"] as? Int) ?? 0
        image = Artwork.resolve(j["displayImage"]) ?? Artwork.resolve(j["images"]) ?? ""; lastPlayed = JSON.string((j["titleHistory"] as? [String: Any])?["lastTimePlayed"])
    }
}
public struct Achievement: Codable, Identifiable, Hashable {
    public var id: String; public var name: String; public var detail: String; public var unlocked: Bool
    public var serviceConfigID: String; public var eventBased: Bool; public var score: String
    public var unlockedAt: Date?
    public init(_ j: [String: Any], legacy: Bool = false) throws {
        id = try JSON.requiredID(j, "id"); name = JSON.string(j["name"])
        detail = JSON.string(j["description"]); if detail.isEmpty { detail = JSON.string(j["lockedDescription"]) }
        unlocked = legacy ? (j["unlocked"] as? Bool ?? false) : JSON.string(j["progressState"]).lowercased() == "achieved"
        serviceConfigID = JSON.string(j["serviceConfigId"])
        let p = j["progression"] as? [String: Any] ?? [:]
        unlockedAt = ServiceDates.parse(JSON.string(p["timeUnlocked"] ?? j["timeUnlocked"]))
        eventBased = (p["requirements"] as? [[String: Any]] ?? []).contains {
            let s = JSON.string($0["id"]); return !s.isEmpty && s != "00000000-0000-0000-0000-000000000000"
        }
        score = JSON.string(j["gamerscore"])
        if score.isEmpty { score = (j["rewards"] as? [[String: Any]] ?? []).first(where: { JSON.string($0["type"]).lowercased() == "gamerscore" }).map { JSON.string($0["value"]) } ?? "" }
    }
}
public struct QueueItem: Codable, Identifiable {
    public var id: String { game.id + "/" + achievement.id }
    public var game: Game; public var achievement: Achievement; public var delay: Double; public var status: String
    public var referenceGamertag: String?
    public var referenceDate: Date?
    public var remainingDelay: Double?
    public init(game: Game, achievement: Achievement, delay: Double) { self.game = game; self.achievement = achievement; self.delay = delay; status = "Waiting" }
}
public enum JSON {
    public static func string(_ v: Any?) -> String { if let s = v as? String { return s }; if let n = v as? NSNumber { return n.stringValue }; return "" }
    public static func numeric(_ s: String) -> Bool { !s.isEmpty && s.utf8.allSatisfy { (48...57).contains($0) } }
    public static func requiredID(_ j: [String: Any], _ key: String) throws -> String {
        let s = string(j[key]); guard numeric(s) else { throw LabsError.message("The service returned an invalid identifier.") }; return s
    }
    public static func object(_ data: Data) throws -> [String: Any] {
        guard let j = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw LabsError.message("Unexpected service response.") }; return j
    }
    public static func data(_ j: Any) throws -> Data { try JSONSerialization.data(withJSONObject: j, options: [.sortedKeys]) }
}
public enum Tokens {
    public static func normalize(_ input: String, event: Bool) throws -> String {
        var value = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if let r = value.range(of: #"(?:tickets\s*:\s*)?"1"\s*=\s*"([^"]+)""#, options: .regularExpression) {
            let match = String(value[r]); if let start = match.range(of: #"=\s*""#, options: .regularExpression) { value = String(match[start.upperBound...].dropLast()) }
        }
        value = value.trimmingCharacters(in: CharacterSet(charactersIn: "\""))
        if value.lowercased().hasPrefix("x:xbl3.0 ") { value = String(value.dropFirst(2)) }
        guard value.range(of: #"(?i)^XBL3\.0\s+x=[^;\s]+;[^\s\x00-\x1F\x7F]+$"#, options: .regularExpression) != nil,
              !value.contains("\"") else { throw LabsError.message("Paste the complete Xbox token, including XBL3.0 x=…;…") }
        try validateExpiry(value)
        return (event ? "x:" : "") + value
    }
    public static func validateExpiry(_ value: String) throws {
        let jwt = value.split(separator: ";").last.map(String.init) ?? ""
        let parts = jwt.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return } // Encrypted event tokens have five segments; Xbox checks them.
        var b = String(parts[1]).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        b += String(repeating: "=", count: (4 - b.count % 4) % 4)
        guard let d = Data(base64Encoded: b), let j = try? JSON.object(d), let exp = j["exp"] as? Double else { return }
        guard exp > Date().timeIntervalSince1970 + 60 else { throw LabsError.message("This token has expired. Paste a current token.") }
    }
}

public enum ServiceDates {
    public static func parse(_ text: String) -> Date? {
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let value = formatter.date(from:text) ?? ISO8601DateFormatter().date(from:text)
        guard let value, value.timeIntervalSince1970 > 0 else { return nil }; return value
    }
}
public enum Artwork {
    public static func resolve(_ value: Any?) -> String? {
        if let text = value as? String {
            let s = text.hasPrefix("//") ? "https:" + text : text
            guard var c = URLComponents(string:s), ["https","http"].contains(c.scheme?.lowercased() ?? ""), c.host != nil, c.user == nil, c.password == nil else { return nil }
            c.scheme = "https"; return c.url?.absoluteString
        }
        if let array = value as? [Any] { return array.lazy.compactMap { resolve($0) }.first }
        if let object = value as? [String:Any] {
            let preferred = ["uri","url","imageUrl","displayImage","BoxArt","boxArt","Tile","tile"]
            for key in preferred + object.keys.sorted().filter({ !preferred.contains($0) }) {
                if let result = resolve(object[key]) { return result }
            }
        }
        return nil
    }
}
public struct TimingPlan {
    public var items: [QueueItem]; public var skipped: Int
    public static func build(game: Game, own: [Achievement], reference: [Achievement], gamertag: String, supported: (Achievement) -> Bool) throws -> TimingPlan {
        guard !game.legacy else { throw LabsError.message("Legacy titles are read-only on mobile.") }
        let grouped = Dictionary(grouping:own,by: \.id)
        let rows = reference.filter { $0.unlocked && $0.unlockedAt != nil }.sorted {
            if $0.unlockedAt != $1.unlockedAt { return $0.unlockedAt! < $1.unlockedAt! }
            return $0.id.localizedStandardCompare($1.id) == .orderedAscending
        }
        guard !rows.isEmpty else { throw LabsError.message("This profile has no visible dated unlocks for this title. Xbox privacy settings may hide them.") }
        var items: [QueueItem] = []; var previous: Date?; var seen = Set<String>(); var skipped = 0
        for row in rows {
            guard seen.insert(row.id).inserted, let matches = grouped[row.id], matches.count == 1, let mine = matches.first,
                Catalogue.normalized(mine.name) == Catalogue.normalized(row.name),
                mine.serviceConfigID.isEmpty || row.serviceConfigID.isEmpty || mine.serviceConfigID.caseInsensitiveCompare(row.serviceConfigID) == .orderedSame,
                !mine.unlocked, supported(mine) else { skipped += 1; continue }
            let date = row.unlockedAt!
            let delay = previous.map { date.timeIntervalSince($0) } ?? 0
            guard delay >= 0, delay <= 31_536_000 else { throw LabsError.message("Reference timing contains an unsupported interval longer than a year. Existing queue retained.") }
            var item = QueueItem(game:game,achievement:mine,delay:delay)
            item.referenceGamertag = gamertag; item.referenceDate = date
            items.append(item); previous = date
        }
        return TimingPlan(items:items,skipped:skipped)
    }
}

