import Foundation
import CryptoKit

public enum LoginProfile: String, CaseIterable, Identifiable {
    case desktop = "Xbox desktop"
    case ios = "Xbox iOS"
    public var id: String { rawValue }
    public var clientID: String { self == .desktop ? "000000004424da1f" : "000000004c12ae6f" }
    public var deviceType: String { self == .desktop ? "Win32" : "iOS" }
}
/// Uses existing public OAuth profiles and their registered desktop redirect.
public final class BrowserLogin {
    private let verifier: String
    private let state: String
    private var consumed = false
    private let created = Date()
    private let session: URLSession
    private let profile: LoginProfile
    private var client: String { profile.clientID }
    private static let scope = "service::user.auth.xboxlive.com::MBI_SSL"
    private static let redirect = "https://login.live.com/oauth20_desktop.srf"
    private static func b64(_ d: Data) -> String { d.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "") }
    public init(session: URLSession = .shared, profile: LoginProfile = .ios) {
        self.profile = profile
        self.session = session
        verifier = Self.b64(Data((0..<32).map { _ in UInt8.random(in: 0...255) }))
        state = Self.b64(Data((0..<32).map { _ in UInt8.random(in: 0...255) }))
    }
    public var startURL: URL {
        var c = URLComponents(string: "https://login.live.com/oauth20_authorize.srf")!
        c.queryItems = ["client_id":client, "scope":Self.scope, "tenant":"consumers", "response_type":"code", "response_mode":"query", "prompt":"select_account", "state":state, "code_challenge":Self.b64(Data(SHA256.hash(data: Data(verifier.utf8)))), "code_challenge_method":"S256", "redirect_uri":Self.redirect].map { URLQueryItem(name:$0.key, value:$0.value) }
        return c.url!
    }
    public static func validateRedirect(_ input: String, state: String) throws -> String {
        guard let c = URLComponents(string: input.trimmingCharacters(in: .whitespacesAndNewlines)), c.scheme == "https", c.host?.lowercased() == "login.live.com", c.path == "/oauth20_desktop.srf", c.port == nil || c.port == 443, c.user == nil, c.password == nil, c.fragment == nil else { throw LabsError.message("Paste the final Microsoft sign-in URL from the browser address bar.") }
        let q = c.queryItems ?? []
        let states = q.filter { $0.name == "state" }; let codes = q.filter { $0.name == "code" }
        guard states.count == 1, states[0].value == state, codes.count == 1, let code = codes[0].value, !code.isEmpty, !q.contains(where: { $0.name == "error" }) else { throw LabsError.message("This sign-in response is invalid, cancelled or belongs to another attempt. Start sign-in again.") }
        return code
    }
    private func post(_ url: String, body: Data, signature: String? = nil, form: Bool = false, stage: String) async throws -> [String:Any] {
        var r = URLRequest(url: URL(string:url)!); r.httpMethod = "POST"; r.timeoutInterval = 45; r.httpBody = body
        r.setValue(form ? "application/x-www-form-urlencoded" : "application/json", forHTTPHeaderField:"Content-Type")
        r.setValue("1", forHTTPHeaderField:"x-xbl-contract-version")
        if let signature { r.setValue(signature, forHTTPHeaderField:"Signature") }
        let (data,response) = try await session.data(for:r)
        guard let h = response as? HTTPURLResponse, (200...299).contains(h.statusCode) else { throw LabsError.message("\(stage) failed (HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)). Start a new browser sign-in. Your saved account was retained.") }
        guard data.count <= 1024 * 1024 else { throw LabsError.message("Authorization response is too large.") }; return try JSON.object(data)
    }
    public func complete(_ redirect: String) async throws -> Account {
        guard !consumed, Date().timeIntervalSince(created) < 600 else { throw LabsError.message("Start a new browser sign-in.") }
        let code = try Self.validateRedirect(redirect, state:state); consumed = true
        var form = URLComponents(); form.queryItems = ["client_id":client,"scope":Self.scope,"code":code,"code_verifier":verifier,"redirect_uri":Self.redirect,"grant_type":"authorization_code"].map { URLQueryItem(name:$0.key,value:$0.value) }
        let encoded = form.percentEncodedQuery!.replacingOccurrences(of:"+",with:"%2B")
        let oauth = try await post("https://login.live.com/oauth20_token.srf", body:Data(encoded.utf8), form:true, stage:"Microsoft sign-in")
        var account = try await authorize(oauth)
        account.refreshToken = oauth["refresh_token"] as? String
        return account
    }
    /// Persist rotation before Xbox exchange, which may fail independently after Microsoft accepts refresh.
    public func refresh(_ previous: Account, persistRotation: (Account) async throws -> Void) async throws -> Account {
        guard previous.loginProfile == profile.rawValue, let refresh = previous.refreshToken, !refresh.isEmpty else { throw LabsError.message("Sign in once with Remember sign-in enabled to allow renewal.") }
        var form = URLComponents(); form.queryItems = ["client_id":client,"scope":Self.scope,"refresh_token":refresh,"grant_type":"refresh_token"].map { URLQueryItem(name:$0.key,value:$0.value) }
        let encoded = form.percentEncodedQuery!.replacingOccurrences(of:"+",with:"%2B")
        let oauth = try await post("https://login.live.com/oauth20_token.srf",body:Data(encoded.utf8),form:true,stage:"Remembered Microsoft sign-in")
        var rotated = previous
        if let next = oauth["refresh_token"] as? String, !next.isEmpty { rotated.refreshToken = next }
        try await persistRotation(rotated)
        var account = try await authorize(oauth)
        guard account.xuid == previous.xuid else { throw LabsError.message("Renewed sign-in belongs to a different Xbox account. The active account and queue were retained; sign in explicitly to switch accounts.") }
        account.refreshToken = rotated.refreshToken; account.eventToken = previous.eventToken
        account.eventTokenExpiresAt = previous.eventTokenExpiresAt; account.eventTokenExpiryHash = previous.eventTokenExpiryHash
        account.presenceAuthorizationFlow = previous.presenceAuthorizationFlow; account.presenceSigningKey = previous.presenceSigningKey; account.presenceAuthorization = previous.presenceAuthorization; account.presenceAuthorizationExpiresAt = previous.presenceAuthorizationExpiresAt
        return account
    }
    /// Explicit authentication-only experiment. Does not send presence or achievement events.
    public func acquireEventToken(_ previous: Account, persistRotation: (Account) async throws -> Void) async throws -> Account {
        try await acquireMobileAuthorization(previous, includeEvents:true, persistRotation:persistRotation)
    }
    /// Experimental app/title + device authorization on this device. Does not send a heartbeat.
    public func acquirePresenceAuthorization(_ previous: Account, persistRotation: (Account) async throws -> Void) async throws -> Account {
        try await acquireMobileAuthorization(previous, includeEvents:false, persistRotation:persistRotation)
    }
    private func acquireMobileAuthorization(_ previous: Account, includeEvents: Bool, persistRotation: (Account) async throws -> Void) async throws -> Account {
        guard (!includeEvents || profile == .ios), previous.loginProfile == profile.rawValue, let refresh = previous.refreshToken, !refresh.isEmpty else {
            throw LabsError.message(includeEvents ? "Sign in with the Xbox iOS browser profile and Remember sign-in enabled first. PC QR/manual sessions cannot run this test." : "Sign in with Microsoft on this device with Remember sign-in enabled before starting spoofing.")
        }
        var form = URLComponents(); form.queryItems = ["client_id":client,"scope":Self.scope,"refresh_token":refresh,"grant_type":"refresh_token"].map { URLQueryItem(name:$0.key,value:$0.value) }
        let oauth = try await post("https://login.live.com/oauth20_token.srf",body:Data(form.percentEncodedQuery!.replacingOccurrences(of:"+",with:"%2B").utf8),form:true,stage:includeEvents ? "Event token: Microsoft renewal" : "Presence: Microsoft renewal")
        var result = previous
        if let next = oauth["refresh_token"] as? String, !next.isEmpty { result.refreshToken = next }
        try await persistRotation(result)
        guard let access = oauth["access_token"] as? String, !access.isEmpty else { throw LabsError.message("Microsoft returned no access token. Existing event token retained.") }
        let key = P256.Signing.PrivateKey(); let pub = key.publicKey.x963Representation
        let proof = ["use":"sig","alg":"ES256","kty":"EC","crv":"P-256","x":Self.b64(pub.subdata(in:1..<33)),"y":Self.b64(pub.subdata(in:33..<65))]
        func request(_ url: String, _ object: [String:Any], signed: Bool = false, stage: String) async throws -> [String:Any] {
            let body = try JSON.data(object); var header: String?
            if signed {
                var version = UInt32(1).bigEndian
                var timestamp = UInt64((Int64(Date().timeIntervalSince1970) + 11644473600) * 10000000).bigEndian
                let v = withUnsafeBytes(of:&version) { Data($0) }; let t = withUnsafeBytes(of:&timestamp) { Data($0) }
                var message = v; message.append(0); message.append(t); message.append(0)
                message.append(Data(("POST\0" + URL(string:url)!.path + "\0\0").utf8)); message.append(body); message.append(0)
                var bytes = v; bytes.append(t); bytes.append(try key.signature(for:message).rawRepresentation); header = bytes.base64EncodedString()
            }
            return try await post(url,body:body,signature:header,stage:stage)
        }
        func token(_ response: [String:Any]) throws -> String {
            guard let value = response["Token"] as? String, !value.isEmpty, value.utf8.count <= 131072 else { throw LabsError.message("Missing or oversized Xbox authorization. Existing event token retained.") }; return value
        }
        func claim(_ response: [String:Any]) throws -> [String:Any] {
            guard let claims = response["DisplayClaims"] as? [String:Any] else { throw LabsError.message("Xbox returned no identity claims.") }
            if let rows = claims["xui"] as? [[String:Any]], rows.count == 1 { return rows[0] }
            if let row = claims["xui"] as? [String:Any] { return row }
            throw LabsError.message("Ambiguous Xbox identity. Existing event token retained.")
        }
        let device = try await request("https://device.auth.xboxlive.com/device/authenticate",["RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT","Properties":["AuthMethod":"ProofOfPossession","Id":"{\(UUID().uuidString)}","DeviceType":includeEvents ? "iOS" : profile.deviceType,"Version":"0.0.0","ProofKey":proof,"SerialNumber":"{\(UUID().uuidString)}"]],signed:true,stage:includeEvents ? "Event token: Xbox iOS device authorization" : "Presence: Xbox device authorization")
        let properties: [String:Any]
        if includeEvents {
            let user = try await request("https://user.auth.xboxlive.com/user/authenticate",["RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT","Properties":["AuthMethod":"RPS","SiteName":"user.auth.xboxlive.com","RpsTicket":"t="+access]],stage:"Event token: Xbox user authorization")
            properties = ["SandboxId":"RETAIL","UserTokens":[try token(user)],"DeviceToken":try token(device)]
        } else {
            // Keep the selected OAuth app, matching device type and proof key together.
            // A user/device-only XSTS loses the app/title identity and was refused by heartbeat.
            let sisu = try await request("https://sisu.xboxlive.com/authorize",["AccessToken":"t="+access,"AppId":client,"DeviceToken":try token(device),"Sandbox":"RETAIL","UseModernGamertag":true,"SiteName":"user.auth.xboxlive.com","RelyingParty":"http://xboxlive.com","ProofKey":proof],signed:true,stage:"Presence: Xbox app/title authorization")
            guard let user = sisu["UserToken"] as? [String:Any], let title = sisu["TitleToken"] as? [String:Any] else { throw LabsError.message("Xbox did not return app/title authorization for this sign-in profile. No heartbeat was sent. Existing account and event token retained.") }
            properties = ["SandboxId":"RETAIL","UserTokens":[try token(user)],"DeviceToken":try token(device),"TitleToken":try token(title),"ProofKey":proof]
        }
        let identity = try await request("https://xsts.auth.xboxlive.com/xsts/authorize",["RelyingParty":"http://xboxlive.com","TokenType":"JWT","Properties":properties],signed:!includeEvents,stage:includeEvents ? "Event token: Xbox identity authorization" : "Presence: Xbox title/device authorization")
        let id = try claim(identity)
        guard let hash = id["uhs"] as? String, !hash.isEmpty else { throw LabsError.message("Xbox returned no user identity.") }
        let authorization = try Tokens.normalize("XBL3.0 x=\(hash);\(try token(identity))",event:false)
        let verified = try await XboxService(session:session).connect(authorization)
        guard verified.xuid == previous.xuid, id["xid"] == nil || JSON.string(id["xid"]) == previous.xuid else { throw LabsError.message("Xbox account mismatch. Existing account and event token retained.") }
        if !includeEvents {
            guard let expiry = ServiceDates.parse(JSON.string(identity["NotAfter"])), expiry > Date().addingTimeInterval(60) else { throw LabsError.message("Presence authorization expiry is missing or invalid. Sign in again on this device.") }
            result.presenceAuthorizationFlow = "title-device-v1"; result.presenceAuthorization = authorization; result.presenceAuthorizationExpiresAt = expiry; result.presenceSigningKey = key.rawRepresentation
            return result
        }
        let events = try await request("https://xsts.auth.xboxlive.com/xsts/authorize",["RelyingParty":"http://events.xboxlive.com","TokenType":"JWT","Properties":properties],stage:"Xbox event-token authorization")
        let eventId = try claim(events)
        guard eventId["uhs"] as? String == hash, eventId["xid"] == nil || JSON.string(eventId["xid"]) == previous.xuid else { throw LabsError.message("Event account mismatch. Existing event token retained.") }
        guard let expiry = ServiceDates.parse(JSON.string(events["NotAfter"])), expiry > Date().addingTimeInterval(60) else { throw LabsError.message("Event-token expiry missing, invalid or too soon. Existing event token retained.") }
        result.authorization = authorization; result.authorizationExpiresAt = ServiceDates.parse(JSON.string(identity["NotAfter"]))
        result.eventToken = try Tokens.normalize("x:XBL3.0 x=\(hash);\(try token(events))",event:true)
        result.eventTokenExpiresAt = expiry; result.eventTokenExpiryHash = EventTokenLifetime.fingerprint(result.eventToken)
        return result
    }
    private func authorize(_ oauth: [String:Any]) async throws -> Account {
        guard let access = oauth["access_token"] as? String, !access.isEmpty else { throw LabsError.message("Microsoft did not return an access token.") }
        let key = P256.Signing.PrivateKey(); let pub = key.publicKey.x963Representation
        let proof = ["use":"sig","alg":"ES256","kty":"EC","crv":"P-256","x":Self.b64(pub.subdata(in:1..<33)),"y":Self.b64(pub.subdata(in:33..<65))]
        func signed(_ url: String, _ object: [String:Any], stage: String) async throws -> [String:Any] {
            let body = try JSON.data(object)
            var version = UInt32(1).bigEndian
            var timestamp = UInt64((Int64(Date().timeIntervalSince1970) + 11644473600) * 10000000).bigEndian
            let v = withUnsafeBytes(of:&version) { Data($0) }; let t = withUnsafeBytes(of:&timestamp) { Data($0) }
            var message = v; message.append(0); message.append(t); message.append(0)
            message.append(Data(("POST\0" + URL(string:url)!.path + "\0\0").utf8)); message.append(body); message.append(0)
            let signature = try key.signature(for:message).rawRepresentation
            var header = v; header.append(t); header.append(signature)
            return try await post(url,body:body,signature:header.base64EncodedString(),stage:stage)
        }
        let device = try await signed("https://device.auth.xboxlive.com/device/authenticate", ["RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT","Properties":["AuthMethod":"ProofOfPossession","Id":"{\(UUID().uuidString)}","DeviceType":profile.deviceType,"Version":"0.0.0","ProofKey":proof,"SerialNumber":"{\(UUID().uuidString)}"]], stage:"Xbox device authorization")
        guard let deviceToken = device["Token"] as? String else { throw LabsError.message("Xbox did not return device authorization.") }
        let xbox = try await signed("https://sisu.xboxlive.com/authorize", ["AccessToken":"t="+access,"AppId":client,"DeviceToken":deviceToken,"Sandbox":"RETAIL","UseModernGamertag":true,"SiteName":"user.auth.xboxlive.com","RelyingParty":"http://xboxlive.com","ProofKey":proof], stage:"Xbox account authorization")
        guard let authorization = xbox["AuthorizationToken"] as? [String:Any], let token = authorization["Token"] as? String, let claims = authorization["DisplayClaims"] as? [String:Any] else { throw LabsError.message("Xbox returned incomplete authorization.") }
        let xui = (claims["xui"] as? [[String:Any]])?.first ?? claims["xui"] as? [String:Any]
        guard let hash = xui?["uhs"] as? String, !hash.isEmpty else { throw LabsError.message("Xbox returned no account identity.") }
        var account = try await XboxService(session:session).connect("XBL3.0 x=\(hash);\(token)")
        account.loginProfile = profile.rawValue
        account.authorizationExpiresAt = ServiceDates.parse(JSON.string(authorization["NotAfter"] ?? authorization["ExpireOn"]))
        return account
    }
}
