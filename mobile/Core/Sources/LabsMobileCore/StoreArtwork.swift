import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Public Store metadata; never includes Xbox authorization.
public struct StoreArtwork: Equatable, Sendable {
    public let url: URL
    public let width: Double
    public let height: Double
    public let purpose: String
    public var ratio: Double { width / height }
    public static func images(_ product: [String: Any]) -> [StoreArtwork] {
        (product["LocalizedProperties"] as? [[String: Any]] ?? []).flatMap { locale in
            (locale["Images"] as? [[String: Any]] ?? []).compactMap { image in
                guard let w = image["Width"] as? Double, let h = image["Height"] as? Double,
                      w >= 300, h >= 300, let raw = image["Uri"] as? String,
                      let url = secureURL(raw) else { return nil }
                return StoreArtwork(url: url, width: w, height: h, purpose: image["ImagePurpose"] as? String ?? "")
            }
        }
    }
    public static func secureURL(_ raw: String) -> URL? {
        guard let url = URL(string: raw.hasPrefix("//") ? "https:" + raw : raw), url.scheme == "https",
              url.user == nil, url.password == nil, let host = url.host?.lowercased(),
              host == "store-images.s-microsoft.com" || host.hasSuffix(".xboxlive.com") || host.hasSuffix(".microsoft.com") else { return nil }
        return url
    }
    public static func best(_ candidates: [StoreArtwork], width: Double, height: Double) -> URL? {
        guard width > 0, height > 0 else { return nil }
        let ratio = width / height
        let purposes = ratio < 1 ? ["Poster", "BoxArt", "TitledHeroArt", "SuperHeroArt"] : ["SuperHeroArt", "TitledHeroArt", "Hero", "FeaturePromotionalArt", "BoxArt", "Poster"]
        return candidates.filter { purposes.contains($0.purpose) }.sorted { a, b in
            let aShape = ratio < 1 ? a.ratio < 1 : a.ratio >= 1.4
            let bShape = ratio < 1 ? b.ratio < 1 : b.ratio >= 1.4
            if aShape != bShape { return aShape }
            let ac = abs(log(a.ratio / ratio)), bc = abs(log(b.ratio / ratio))
            if abs(ac - bc) > 0.05 { return ac < bc }
            let ap = purposes.firstIndex(of: a.purpose) ?? 99, bp = purposes.firstIndex(of: b.purpose) ?? 99
            if ap != bp { return ap < bp }
            return a.width > b.width
        }.first?.url
    }
    public static func lookup(_ title: String, session: URLSession = .shared) async throws -> [StoreArtwork] {
        guard let value = UInt32(title), value > 0 else { return [] }
        func read(_ url: URL) async throws -> [String: Any] {
            var request = URLRequest(url: url); request.timeoutInterval = 15
            let (bytes, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200, bytes.count <= 4 * 1024 * 1024 else { throw LabsError.message("Store artwork unavailable.") }
            return try JSONSerialization.jsonObject(with: bytes) as? [String: Any] ?? [:]
        }
        let data = try await read(URL(string: "https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=XboxTitleId&Value=\(value)&market=US&languages=en-US")!)
        var result: [StoreArtwork] = []
        for product in (data["Products"] as? [[String: Any]] ?? []).prefix(4) {
            var found = images(product)
            if !(found.contains { $0.ratio < 1 } && found.contains { $0.ratio >= 1.4 }), let id = product["ProductId"] as? String,
               id.range(of: "^[A-Za-z0-9]{1,32}$", options: .regularExpression) != nil,
               let detail = try? await read(URL(string: "https://displaycatalog.mp.microsoft.com/v7.0/products/\(id)?market=US&languages=en-US")!),
               let full = detail["Product"] as? [String: Any] { found += images(full) }
            result += found
        }
        return result
    }
}
