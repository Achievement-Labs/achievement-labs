import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public struct XboxService {
    private let session: URLSession
    public init(session: URLSession = XboxSessions.shared) { self.session = session }
    private func request(_ url: String, authorization: String, version: String = "2", method: String = "GET", body: Any? = nil, presenceKey: Data? = nil) async throws -> Data {
        guard let endpoint = URL(string: url), endpoint.scheme == "https", endpoint.host?.hasSuffix(".xboxlive.com") == true else { throw LabsError.message("Invalid Xbox endpoint.") }
        var r = URLRequest(url: endpoint); r.timeoutInterval = 45; r.httpMethod = method
        r.setValue(authorization, forHTTPHeaderField: "Authorization"); r.setValue(version, forHTTPHeaderField: "x-xbl-contract-version")
        r.setValue("application/json", forHTTPHeaderField: "Accept"); r.setValue("en-US", forHTTPHeaderField: "Accept-Language")
        if let body { r.httpBody = try JSON.data(body); r.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        if let presenceKey { r.setValue(try PresenceSignature.header(method:method,url:endpoint,authorization:authorization,body:r.httpBody ?? Data(),key:presenceKey),forHTTPHeaderField:"Signature") }
        let (data, response) = try await transfer(r)
        try Self.check(response, data:data, endpoint: endpoint.host ?? "Xbox"); guard data.count <= 128 * 1024 * 1024 else { throw LabsError.message("The response is too large.") }; return data
    }
    private func transfer(_ request: URLRequest) async throws -> (Data,URLResponse) {
        do { return try await session.data(for:request) }
        catch {
            let e = error as NSError
            if e.domain == NSURLErrorDomain, (-1206 ... -1200).contains(e.code) { throw XboxTransportFailure(error,endpoint:request.url?.host ?? "Xbox") }
            throw error
        }
    }
    /// Connectivity only: no authorization, event token, payload or unlock request.
    public func probe(_ url: URL) async -> String {
        let allowed = ["mobile.events.data.microsoft.com","v20.events.data.microsoft.com","achievements.xboxlive.com","presence-heartbeat.xboxlive.com"]
        guard url.scheme == "https", allowed.contains(url.host ?? ""), url.user == nil, url.password == nil else { return "Unsupported probe host." }
        var r = URLRequest(url:url); r.httpMethod = "HEAD"; r.timeoutInterval = 15
        do { let (_,response) = try await transfer(r); return "\(url.host!) · TLS accepted · HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)" }
        catch { return "\(url.host!) · \(error.localizedDescription)" }
    }
    public func checkCollector(_ route: CollectorRoute) async throws {
        var r = URLRequest(url:route.url); r.httpMethod = "HEAD"; r.timeoutInterval = 15
        _ = try await transfer(r) // Any HTTP response proves TLS; this sends no event or credentials.
    }
    private static func check(_ response: URLResponse, data: Data = Data(), event: Bool = false, endpoint: String = "Xbox") throws {
        guard let http = response as? HTTPURLResponse else { throw LabsError.message("No HTTP response.") }
        guard (200...299).contains(http.statusCode) else {
            let detail: String
            if endpoint == "presence-heartbeat.xboxlive.com", http.statusCode == 403 {
                detail = "Xbox refused title spoofing (403). A working library login does not guarantee heartbeat access. See Spoofing diagnostics for the server code and independent login check."
            } else if http.statusCode == 401 {
                detail = event ? "The collector rejected authorization (401). Check the event token and account authorization." : "Xbox account authorization expired or was rejected (401). Sign in again."
            } else if http.statusCode == 403 {
                detail = event ? "Xbox refused the event request (403)." : "Xbox refused access to \(endpoint) (403). Permissions or profile privacy may restrict this request."
            } else { detail = "Xbox returned HTTP \(http.statusCode) from \(endpoint)." }
            throw XboxHTTPFailure(status:http.statusCode,endpoint:endpoint,message:detail,response:http,data:data)
        }
    }
    public func connect(_ token: String) async throws -> Account {
        let auth = try Tokens.normalize(token, event: false)
        let data = try await request("https://profile.xboxlive.com/users/me/profile/settings?settings=Gamertag,GameDisplayPicRaw,Gamerscore", authorization: auth)
        let root = try JSON.object(data)
        guard let profile = (root["profileUsers"] as? [[String: Any]])?.first else { throw LabsError.message("Xbox did not return an account.") }
        let xuid = try JSON.requiredID(profile, "id")
        let settings = profile["settings"] as? [[String: Any]] ?? []
        let name = settings.first { JSON.string($0["id"]) == "Gamertag" }.map { JSON.string($0["value"]) } ?? "Xbox account"
        var account = Account(authorization: auth, xuid: xuid, gamertag: name)
        account.gamerpic = settings.first { JSON.string($0["id"]) == "GameDisplayPicRaw" }.flatMap { Artwork.resolve($0["value"]) }
        account.gamerscore = settings.first { JSON.string($0["id"]) == "Gamerscore" }.map { JSON.string($0["value"]) }
        return account
    }
    public func games(_ account: Account) async throws -> [Game] {
        guard JSON.numeric(account.xuid) else { throw LabsError.message("Invalid account.") }
        let data = try await request("https://titlehub.xboxlive.com/users/xuid(\(account.xuid))/titles/titleHistory/decoration/TitleHistory,Achievement,detail,scid?maxItems=10000", authorization: account.authorization)
        let root = try JSON.object(data)
        guard let titles = root["titles"] as? [[String: Any]] else { throw LabsError.message("Library response is incomplete; previous library retained.") }
        return try titles.map(Game.init)
    }
    public func achievements(_ game: Game, account: Account, xuid: String? = nil) async throws -> [Achievement] {
        let target = xuid ?? account.xuid
        guard JSON.numeric(game.id), JSON.numeric(target) else { throw LabsError.message("Invalid title or account.") }
        var results: [Achievement] = []; var continuation: String?; var seen = Set<String>()
        repeat {
            try Task.checkCancellation()
            var components = URLComponents(string: "https://achievements.xboxlive.com/users/xuid(\(target))/\(game.legacy ? "titleachievements" : "achievements")")!
            components.queryItems = [URLQueryItem(name: "titleId", value: game.id), URLQueryItem(name: "maxItems", value: "1000")]
            if let continuation { components.queryItems!.append(URLQueryItem(name: "continuationToken", value: continuation)) }
            let data = try await request(components.url!.absoluteString, authorization: account.authorization, version: game.legacy ? "3" : "4")
            let j = try JSON.object(data)
            guard let rows = j["achievements"] as? [[String: Any]] else { throw LabsError.message("Incomplete achievement response. Previous progress retained.") }
            results += try rows.map { try Achievement($0, legacy: game.legacy) }
            continuation = (j["pagingInfo"] as? [String: Any])?["continuationToken"] as? String
            if continuation == "" { continuation = nil }
            if let continuation { guard seen.insert(continuation).inserted, seen.count < 100 else { throw LabsError.message("Invalid achievement pagination.") } }
        } while continuation != nil
        return results
    }
    public func profile(gamertag: String, account: Account) async throws -> Account {
        let tag = gamertag.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !tag.isEmpty, tag.count <= 64 else { throw LabsError.message("Enter a gamertag.") }
        let encoded = tag.addingPercentEncoding(withAllowedCharacters: .alphanumerics)!
        let data = try await request("https://profile.xboxlive.com/users/gt(\(encoded))/profile/settings?settings=Gamertag", authorization:account.authorization)
        guard let profile = (try JSON.object(data)["profileUsers"] as? [[String:Any]])?.first else { throw LabsError.message("Gamertag not found.") }
        let xuid = try JSON.requiredID(profile,"id")
        let settings = profile["settings"] as? [[String:Any]] ?? []
        let name = settings.first { JSON.string($0["id"]) == "Gamertag" }.map { JSON.string($0["value"]) } ?? tag
        return Account(authorization:account.authorization,xuid:xuid,gamertag:name)
    }
    /// Read-only reference dates for an exact 360 title. No profile file is uploaded.
    public func reference360(_ title:String,gamertag:String,account:Account) async throws -> [[String:Any]] {
        guard JSON.numeric(title),UInt32(title) != nil else { throw LabsError.message("Invalid Xbox 360 title.") }
        let user = try await profile(gamertag:gamertag,account:account)
        var output:[[String:Any]] = [], continuation = "", seen = Set<String>()
        repeat {
            var url = URLComponents(string:"https://achievements.xboxlive.com/users/xuid(\(user.xuid))/titleachievements")!
            url.queryItems = [URLQueryItem(name:"titleId",value:title),URLQueryItem(name:"maxItems",value:"1000")]
            if !continuation.isEmpty { url.queryItems!.append(URLQueryItem(name:"continuationToken",value:continuation)) }
            let data = try await request(url.url!.absoluteString,authorization:account.authorization,version:"3")
            let json = try JSON.object(data)
            guard let rows = json["achievements"] as? [[String:Any]] else { throw LabsError.message("Incomplete reference response.") }
            for row in rows where JSON.string(row["titleId"]) == title && row["unlocked"] as? Bool == true {
                guard let id = UInt32(JSON.string(row["id"])),let score = UInt32(JSON.string(row["gamerscore"])) else { continue }
                let online = row["unlockedOnline"] as? Bool ?? false
                let date = ServiceDates.parse(JSON.string(row["timeUnlocked"]))
                if online && date == nil { continue }
                output.append(["id":id,"titleId":title,"score":score,"unlocked":true,"online":online,"time":online ? (date!.timeIntervalSince1970*1000).rounded() as Any : NSNull()])
            }
            continuation = (json["pagingInfo"] as? [String:Any])?["continuationToken"] as? String ?? ""
            if !continuation.isEmpty { guard continuation != "null",seen.insert(continuation).inserted,seen.count < 100 else { throw LabsError.message("Invalid reference pagination.") } }
            guard output.count <= 5000 else { throw LabsError.message("Too many reference achievements.") }
        } while !continuation.isEmpty
        return output
    }
    public func game(_ title: String, account: Account) async throws -> Game {
        guard JSON.numeric(title) else { throw LabsError.message("Enter a numeric Title ID.") }
        let data = try await request("https://titlehub.xboxlive.com/users/xuid(\(account.xuid))/titles/batch/decoration/TitleHistory,Achievement,detail,scid", authorization:account.authorization, method:"POST", body:["Pfns":NSNull(),"TitleIds":[title]])
        guard let row = (try JSON.object(data)["titles"] as? [[String:Any]])?.first(where: { JSON.string($0["titleId"]) == title }) else { throw LabsError.message("Xbox did not return this title.") }
        return try Game(row)
    }
    public func playedMinutes(_ title: String, account: Account) async throws -> Double? {
        guard JSON.numeric(title) else { throw LabsError.message("Invalid Title ID.") }
        let data = try await request("https://userstats.xboxlive.com/batch", authorization:account.authorization, method:"POST", body:["ArrangeByField":"xuid","Xuids":[account.xuid],"Stats":[["Name":"MinutesPlayed","TitleId":title]]])
        func find(_ value: Any) -> Double? {
            if let object = value as? [String:Any] {
                if JSON.string(object["name"] ?? object["Name"]).caseInsensitiveCompare("MinutesPlayed") == .orderedSame, let result = Double(JSON.string(object["value"] ?? object["Value"])), result.isFinite, result >= 0 { return result }
                for key in object.keys.sorted() { if let result = find(object[key]!) { return result } }
            } else if let array = value as? [Any] { for item in array { if let result = find(item) { return result } } }
            return nil
        }
        return find(try JSON.object(data))
    }
    public func titleUnlock(_ achievement: Achievement, game: Game, account: Account) async throws {
        guard !game.legacy, !achievement.eventBased, JSON.numeric(game.id), JSON.numeric(achievement.id), UUID(uuidString: achievement.serviceConfigID) != nil else { throw LabsError.message("This achievement cannot use title-based unlocks.") }
        _ = try await request("https://achievements.xboxlive.com/users/xuid(\(account.xuid))/achievements/\(achievement.serviceConfigID)/update", authorization: account.authorization, method: "POST", body: ["action":"progressUpdate", "serviceConfigId":achievement.serviceConfigID, "titleId":game.id, "userId":account.xuid, "achievements":[["id":achievement.id,"percentComplete":"100"]]])
    }
    public func presence(_ title: String?, account: Account) async throws {
        if let title { guard JSON.numeric(title) else { throw LabsError.message("Enter a numeric Title ID.") } }
        if account.presenceAuthorization != nil && account.presenceSigningKey == nil { throw LabsError.message("Renew mobile presence authorization before spoofing.") }
        let body: Any? = title.map { ["titles":[["id":$0,"expiration":600,"state":"active","sandbox":"RETAIL"]]] }
        _ = try await request("https://presence-heartbeat.xboxlive.com/users/xuid(\(account.xuid))/devices/current/", authorization: account.presenceAuthorization ?? account.authorization, version: "3", method: title == nil ? "DELETE" : "POST", body: body, presenceKey:account.presenceSigningKey)
    }
    public func sendEvent(_ payload: String, account: Account, route: CollectorRoute = .windows) async throws {
        let token = try Tokens.normalize(account.eventToken, event: true)
        var r = URLRequest(url:route.url)
        r.httpMethod = "POST"; r.timeoutInterval = 45
        // One JSON record per line; never transmit pretty-printed template lines.
        r.httpBody = try JSON.data(JSON.object(Data(payload.utf8)))
        let redacted = account.authorization.replacingOccurrences(of: #"(?i)x=[^;]+;"#, with: "x=-;", options: .regularExpression)
        let headers = ["Content-Type":"application/x-json-stream; charset=utf-8", "Accept":"application/json", "User-Agent":"MSDW", "reliability-mode":"standard", "Client-Id":"NO_AUTH", "client-version":"EUTC-Windows-C++-no-10.0.22621.3296.amd64fre.ni_release.220506-1250-no", "apikey":"0890af88a9ed4cc886a14f5e174a2827-9de66c5e-f867-43a8-a7b8-e0ddd481cca4-7548,95c1f21d6cb047a09e7b423c1cb2222e-9965f07b-54fa-498e-9727-9e8d24dec39e-7027", "authxtoken":redacted, "tickets":"\"1\"=\"\(token)\""]
        headers.forEach { r.setValue($1, forHTTPHeaderField: $0) }

        let (data, response) = try await transfer(r); try Self.check(response, data:data, event: true,endpoint:route.url.host!); try Self.validateCollector(data)
    }
    public static func validateCollector(_ data: Data) throws {
        guard !data.isEmpty else { return }
        guard data.count <= 65536 else { throw LabsError.message("Collector acknowledgement could not be checked.") }
        guard let j = try? JSON.object(data) else { throw LabsError.message("Unrecognised collector acknowledgement; refresh to verify progress.") }
        let failure = j["TokenCrackingFailure"]
        let badToken = failure != nil && !(failure is NSNull) && (failure as? Bool != false)
        let explicit = (j["efi"] as? [String: Any] ?? [:]).values.contains { ($0 as? String) == "all" || (($0 as? [Any])?.isEmpty == false) }
        if badToken || explicit || (j["rej"] as? Int ?? 0) > 0 || (j["acc"] as? Int) == 0 { throw LabsError.message("Collector rejected the event. Refresh your token or check the mapping; no unlock is confirmed.") }
    }
}

public struct XboxHTTPFailure: LocalizedError {
    public let status: Int
    public let endpoint: String
    public let message: String
    public let serverCode: String?
    public let correlation: String?
    public var errorDescription: String? { message }
    public var diagnostic: String {
        "HTTP \(status) · \(endpoint)" + (serverCode.map { "\nServer code: " + $0 } ?? "\nNo structured server error code supplied.") + (correlation.map { "\nCorrelation: " + $0 } ?? "")
    }
    init(status: Int, endpoint: String, message: String, response: HTTPURLResponse, data: Data) {
        self.status = status; self.endpoint = endpoint; self.message = message
        func safeCode(_ value: String?) -> String? {
            guard let value, !value.isEmpty, value.utf8.count <= 64, value.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || [45,95,46].contains($0) }) else { return nil }; return value
        }
        let json = data.count <= 65536 ? (try? JSON.object(data)) : nil
        let error = json?["error"] as? [String:Any]
        serverCode = safeCode(response.value(forHTTPHeaderField:"x-xbl-error-code")) ?? safeCode(JSON.string(json?["XErr"] ?? json?["code"] ?? json?["errorCode"] ?? error?["code"]))
        let header = response.value(forHTTPHeaderField:"x-ms-request-id") ?? response.value(forHTTPHeaderField:"ms-cv")
        if let header, header.count <= 128, header.range(of:#"^[A-Za-z0-9+/.=_-]+$"#,options:.regularExpression) != nil { correlation = header } else { correlation = nil }
    }
}

