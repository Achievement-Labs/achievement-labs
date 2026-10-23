import Foundation
import CryptoKit

public struct EventFeed: Codable {
    public let schemaVersion: Int
    public let upstreamRelease: String
    public let archiveSHA256: String
    public let generatedAt: String
    public var titles: [String:SyncedTitle]
}
public struct SyncedTitle: Codable { public let template: String; public let recipes: MobileRecipes }
public struct MobileRelease: Equatable {
    public let version: String; public let page: URL; public let asset: URL
    public static func newer(_ a: String, than b: String) -> Bool {
        guard let left = parts(a), let right = parts(b) else { return false }
        return left.lexicographicallyPrecedes(right) == false && left != right
    }
    private static func parts(_ value: String) -> [Int]? {
        let chunks = value.split(separator:".",omittingEmptySubsequences:false)
        guard chunks.count == 3 else { return nil }
        let values = chunks.compactMap { text -> Int? in guard JSON.numeric(String(text)), let n = Int(text), n < 1_000_000_000 else { return nil }; return n }
        return values.count == 3 ? values : nil
    }
    public static func latest(_ data: Data) throws -> MobileRelease? {
        guard let releases = try JSONSerialization.jsonObject(with:data) as? [[String:Any]] else { throw LabsError.message("Invalid release list.") }
        var result: MobileRelease?
        for release in releases where release["draft"] as? Bool == false {
            guard let page = URL(string:JSON.string(release["html_url"])), page.scheme == "https", page.host == "github.com", page.path.hasPrefix("/Achievement-Labs/achievement-labs/releases/tag/"), page.user == nil else { continue }
            for asset in release["assets"] as? [[String:Any]] ?? [] {
                let name = JSON.string(asset["name"]); let prefix = "AchievementLabs-iOS-"; let suffix = "-UNSIGNED.ipa"
                guard name.hasPrefix(prefix), name.hasSuffix(suffix) else { continue }
                let version = String(name.dropFirst(prefix.count).dropLast(suffix.count))
                guard parts(version) != nil, let url = URL(string:JSON.string(asset["browser_download_url"])), url.scheme == "https", url.host == "github.com", url.user == nil, url.path.hasPrefix("/Achievement-Labs/achievement-labs/releases/download/"), url.lastPathComponent == name else { continue }
                let candidate = MobileRelease(version:version,page:page,asset:url)
                if result == nil || newer(version,than:result!.version) { result = candidate }
            }
        }
        return result
    }
}
public struct PublicSync {
    private let session: URLSession
    public init(session: URLSession? = nil) {
        if let session { self.session = session }
        else { let config = URLSessionConfiguration.ephemeral; config.httpShouldSetCookies = false; self.session = URLSession(configuration:config) }
    }
    private func get(_ url: String, limit: Int) async throws -> Data {
        var request = URLRequest(url:URL(string:url)!); request.timeoutInterval = 90; request.cachePolicy = .reloadIgnoringLocalCacheData
        request.setValue("AchievementLabs-Mobile/0.3",forHTTPHeaderField:"User-Agent")
        let (data,response) = try await session.data(for:request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode), data.count <= limit else { throw LabsError.message("Public update check failed (HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)). Existing data was retained.") }
        return data
    }
    public func events() async throws -> EventFeed {
        let data = try await get("https://raw.githubusercontent.com/Achievement-Labs/achievement-labs/mobile-event-feed/catalogue.json",limit:32*1024*1024)
        return try JSONDecoder().decode(EventFeed.self,from:data)
    }
    public func searchTitles(_ input: String) async throws -> [Game] {
        if let direct = try TitleSearch.direct(input) { return [direct] }
        return try TitleSearch.parse(await get(TitleSearch.url(input).absoluteString,limit:2*1024*1024))
    }
    public func release() async throws -> MobileRelease? {
        try MobileRelease.latest(await get("https://api.github.com/repos/Achievement-Labs/achievement-labs/releases?per_page=100",limit:4*1024*1024))
    }
}

public struct PCTransfer: Codable {
    public let schemaVersion: Int; public let xuid: String; public let authorization: String; public let eventToken: String
    public let kind: String?
    public var eventOnly: Bool { kind == "eventToken" }
    public var signInOnly: Bool { kind == "signIn" }
    public let expiresAt: Int64; public let authorizationExpiresAt: String?
    public static func open(_ data: Data, key text: String, now: Date = Date()) throws -> PCTransfer {
        guard data.count <= 128*1024 else { throw LabsError.message("Transfer file is too large.") }
        struct Envelope: Decodable { let schemaVersion: Int; let nonce: String; let ciphertext: String; let tag: String }
        let envelope = try JSONDecoder().decode(Envelope.self,from:data)
        let padded = text.trimmingCharacters(in:.whitespacesAndNewlines).replacingOccurrences(of:"-",with:"+").replacingOccurrences(of:"_",with:"/")
        let keyData = Data(base64Encoded:padded + String(repeating:"=",count:(4-padded.count%4)%4))
        guard envelope.schemaVersion == 1, let keyData, keyData.count == 32, let nonce = Data(base64Encoded:envelope.nonce), nonce.count == 12, let ciphertext = Data(base64Encoded:envelope.ciphertext), let tag = Data(base64Encoded:envelope.tag), tag.count == 16 else { throw LabsError.message("Invalid transfer file or pairing key.") }
        let box = try AES.GCM.SealedBox(nonce:AES.GCM.Nonce(data:nonce),ciphertext:ciphertext,tag:tag)
        let plain: Data
        do { plain = try AES.GCM.open(box,using:SymmetricKey(data:keyData),authenticating:Data("AchievementLabs-PC-Transfer-v1".utf8)) }
        catch { throw LabsError.message("The pairing key does not match this file.") }
        let transfer = try JSONDecoder().decode(PCTransfer.self,from:plain)
        guard transfer.schemaVersion == 1, JSON.numeric(transfer.xuid), Double(transfer.expiresAt) > now.timeIntervalSince1970, Double(transfer.expiresAt) <= now.timeIntervalSince1970 + 900 else { throw LabsError.message("The PC transfer expired. Export a fresh file on PC.") }
        if transfer.eventOnly {
            guard transfer.authorization.isEmpty, !transfer.eventToken.isEmpty else { throw LabsError.message("Invalid event-token-only transfer.") }
        } else {
            guard transfer.kind == nil || transfer.kind == "account" || transfer.signInOnly else { throw LabsError.message("Unsupported PC transfer type.") }
            _ = try Tokens.normalize(transfer.authorization,event:false)
        }
        if transfer.signInOnly && !transfer.eventToken.isEmpty { throw LabsError.message("Sign-in-only transfer must not contain an event token.") }
        if !transfer.eventToken.isEmpty { _ = try Tokens.normalize(transfer.eventToken,event:true) }
        return transfer
    }
}

