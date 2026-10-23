import Foundation

public enum TitlePlatform: String, CaseIterable, Identifiable {
    case series, one, xbox360, pc, gfwl, win32, win8, mobile, other
    public var id: String { rawValue }
    public var label: String {
        switch self {
        case .series: return "Xbox Series X|S"
        case .one: return "Xbox One"
        case .xbox360: return "Xbox 360"
        case .pc: return "PC"
        case .gfwl: return "GFWL"
        case .win32: return "Win32"
        case .win8: return "Windows 8"
        case .mobile: return "Mobile"
        case .other: return "Other / unknown"
        }
    }
    public static func groups(_ values: [String]) -> Set<TitlePlatform> {
        let names = values.map { $0.uppercased().replacingOccurrences(of:"[^A-Z0-9]",with:"",options:.regularExpression) }
        if names.contains("GFWL") || names.contains("PC") && names.contains("XBOX360") { return [.gfwl] }
        if names.isEmpty { return [.other] }
        return Set(names.map { name in
            switch name {
            case "XBOXSERIES", "XBOXSERIESXS", "SCARLETT": return .series
            case "XBOXONE": return .one
            case "XBOX360": return .xbox360
            case "PC", "WINDOWS", "WINDOWS10": return .pc
            case "WIN32": return .win32
            case "WIN8", "WINDOWS8", "WINDOWS81": return .win8
            case "MOBILE", "WINDOWSPHONE", "IOS", "ANDROID": return .mobile
            default: return .other
            }
        })
    }
}

public struct TitleSearchResponse {
    public let games: [Game]
    public let partial: Bool
}

/// Public discovery only. Account credentials are used separately by XboxService.
public enum TitleSearch {
    private static let romans = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX", "XXI", "XXII", "XXIII", "XXIV", "XXV", "XXVI", "XXVII", "XXVIII", "XXIX", "XXX"]
    private static let words = try! NSRegularExpression(pattern:"[\\p{L}\\p{N}]+")
    private static func alternate(_ word: String, at start: Int) -> String? {
        if let n = Int(word), (1...30).contains(n), word == String(n) { return romans[n] }
        if let n = romans.firstIndex(of:word.uppercased()), n > 0, !(n == 1 && start == 0) { return String(n) }
        return nil
    }
    public static func key(_ value: String) -> String {
        words.matches(in:value,range:NSRange(value.startIndex...,in:value)).map { match in
            let word = String(value[Range(match.range,in:value)!])
            if let n = romans.firstIndex(of:word.uppercased()), n > 0, !(n == 1 && match.range.location == 0) { return String(n) }
            return word.lowercased()
        }.joined(separator:" ")
    }
    public static func matches(_ name: String, query: String) -> Bool {
        if query.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty { return true }
        let needle = key(query)
        return needle.isEmpty ? name.localizedCaseInsensitiveContains(query) : key(name).contains(needle)
    }
    public static func variants(_ input: String) throws -> [String] {
        let value = try query(input)
        var result = [value]
        func add(_ text: String) {
            if text.count <= 200 && result.count < 8 && !result.contains(where: { $0.caseInsensitiveCompare(text) == .orderedSame }) { result.append(text) }
        }
        let matches = words.matches(in:value,range:NSRange(value.startIndex...,in:value))
        for match in matches.filter({ alternate(String(value[Range($0.range,in:value)!]),at:$0.range.location) != nil }).prefix(6) {
            let range = Range(match.range,in:value)!
            add(String(value[..<range.lowerBound]) + alternate(String(value[range]),at:match.range.location)! + String(value[range.upperBound...]))
        }
        var combined = value
        for match in matches.reversed() {
            if let replacement = alternate(String(value[Range(match.range,in:value)!]),at:match.range.location) {
                combined.replaceSubrange(Range(match.range,in:combined)!,with:replacement)
            }
        }
        add(combined); return result
    }
    public static func merge(_ batches: [[Game]]) -> [Game] {
        var seen = Set<String>(); var result: [Game] = []
        for game in batches.flatMap({ $0 }) where result.count < 300 && seen.insert(game.id).inserted { result.append(game) }
        return result.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }
    public static func applyingProgress(_ game: Game, rows: [Achievement]) -> Game {
        let unlocked = rows.filter(\.unlocked).count
        guard !rows.isEmpty, !game.legacy || unlocked >= game.unlocked else { return game }
        var updated = game; updated.unlocked = unlocked; updated.total = rows.count; return updated
    }
    public static func query(_ input: String) throws -> String {
        let value = input.trimmingCharacters(in:.whitespacesAndNewlines)
        guard !value.isEmpty, value.count <= 200 else { throw LabsError.message("Enter a game name or decimal Xbox Title ID (up to 200 characters).") }
        if JSON.numeric(value), UInt32(value).map({ $0 > 0 }) != true { throw LabsError.message("Enter a Title ID from 1 to 4294967295.") }
        return value
    }
    public static func direct(_ input: String) throws -> Game? {
        let value = try query(input)
        guard JSON.numeric(value), let id = UInt32(value) else { return nil }
        return try Game(["titleId":String(id),"name":"Title \(id)","devices":[String]()])
    }
    public static func url(_ input: String) throws -> URL {
        let value = try query(input)
        var parts = URLComponents(string:"https://dbox.tools/api/title_ids/")!
        parts.queryItems = [URLQueryItem(name:"name",value:value),URLQueryItem(name:"limit",value:"100"),URLQueryItem(name:"offset",value:"0")]
        return parts.url!
    }
    public static func parse(_ data: Data) throws -> [Game] {
        guard data.count <= 2*1024*1024, let rows = try JSON.object(data)["items"] as? [[String:Any]], rows.count <= 100 else { throw LabsError.message("Invalid game search response.") }
        var seen = Set<String>(); var result: [Game] = []
        for row in rows {
            let hex = JSON.string(row["title_id"] ?? row["titleId"])
            guard hex.range(of:#"^[0-9a-fA-F]{1,8}$"#,options:.regularExpression) != nil, let id = UInt32(hex,radix:16), id > 0 else { continue }
            let name = JSON.string(row["name"]).trimmingCharacters(in:.whitespacesAndNewlines)
            guard !name.isEmpty, name.count <= 300, seen.insert(String(id)).inserted else { continue }
            let systems = (row["systems"] as? [String] ?? []).map { $0.uppercased() }
            var platforms = systems.map { ["XBOXONE":"XboxOne","XBOXSERIES":"XboxSeries","SCARLETT":"XboxSeries","XBOX360":"Xbox360","PC":"PC","WINDOWS":"PC","WINDOWS10":"PC","WIN32":"Win32","WINDOWS8":"Windows8","WIN8":"Windows8","WINDOWS81":"Windows8","MOBILE":"Mobile","WINDOWSPHONE":"WindowsPhone","IOS":"iOS","ANDROID":"Android"][ $0 ] ?? $0 }
            if systems.contains("PC") && systems.contains("XBOX360") { platforms.append("GFWL") }
            result.append(try Game(["titleId":String(id),"name":name,"devices":platforms]))
        }
        return result.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }
}
