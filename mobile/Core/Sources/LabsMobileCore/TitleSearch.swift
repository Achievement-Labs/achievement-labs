import Foundation

/// Public discovery only. Account credentials are used separately by XboxService.
public enum TitleSearch {
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
        return try Game(["titleId":String(id),"name":"Title \(id)","devices":["XboxOne"]])
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
            var platforms = systems.compactMap { ["XBOXONE":"XboxOne","XBOXSERIES":"XboxSeries","SCARLETT":"XboxSeries","XBOX360":"Xbox360","PC":"PC","WINDOWS":"PC"][ $0 ] }
            if systems.contains("PC") && systems.contains("XBOX360") { platforms.append("GFWL") }
            result.append(try Game(["titleId":String(id),"name":name,"devices":platforms]))
        }
        return result.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }
}
