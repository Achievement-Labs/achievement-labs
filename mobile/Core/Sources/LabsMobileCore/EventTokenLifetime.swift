import Foundation
import CryptoKit

/// Issuance metadata bound to the exact token; an opaque imported token is never assigned a guessed lifetime.
public enum EventTokenLifetime {
    public static func fingerprint(_ token:String)->String { SHA256.hash(data:Data(token.utf8)).map { String(format:"%02x",$0) }.joined() }
    public static func expiry(_ account:Account)->Date? {
        guard !account.eventToken.isEmpty,account.eventTokenExpiryHash == fingerprint(account.eventToken) else { return nil }
        return account.eventTokenExpiresAt
    }
    public static func status(_ account:Account?, at date:Date = Date())->String {
        guard let account,!account.eventToken.isEmpty else { return "No event token saved." }
        guard let expiry=expiry(account) else { return "Expiry unknown. Get a fresh mobile event token to record its issued expiry." }
        if expiry <= date { return "Issued expiry passed: "+expiry.formatted(date:.abbreviated,time:.shortened)+". Get a fresh event token." }
        let minutes=max(1,Int(ceil(expiry.timeIntervalSince(date)/60)))
        return "Issued expiry: "+expiry.formatted(date:.abbreviated,time:.shortened)+" · about "+(minutes >= 60 ? "\(minutes/60)h \(minutes%60)m":"\(minutes)m")+" remaining."
    }
}
