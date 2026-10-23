import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public enum CollectorRoute: String, CaseIterable, Identifiable {
    case mobile = "Mobile", windows = "Windows"
    public var id: String { rawValue }
    public var url: URL { URL(string:"https://\(self == .mobile ? "mobile" : "v20").events.data.microsoft.com/OneCollector/1.0/")! }
}
public struct XboxTransportFailure: LocalizedError {
    public let endpoint: String
    public let code: Int
    public let streamDomain: Int?
    public let streamCode: Int?
    public var isTLS: Bool { (-1206 ... -1200).contains(code) }
    public var errorDescription: String? { "\(isTLS ? "TLS connection failed" : "Network request failed") · \(endpoint) · URL error \(code)" + (streamCode.map { " · SSL/stream \($0)" } ?? "") + ". No automatic replay was sent." }
    public init(_ error: Error, endpoint: String) {
        self.endpoint = endpoint; let e = error as NSError; code = e.code
        let underlying = e.userInfo[NSUnderlyingErrorKey] as? NSError
        streamDomain = (e.userInfo["_kCFStreamErrorDomainKey"] as? NSNumber)?.intValue ?? (underlying?.userInfo["_kCFStreamErrorDomainKey"] as? NSNumber)?.intValue
        streamCode = (e.userInfo["_kCFStreamErrorCodeKey"] as? NSNumber)?.intValue ?? (underlying?.userInfo["_kCFStreamErrorCodeKey"] as? NSNumber)?.intValue
    }
}
public enum QueueDelayInput {
    public static func seconds(_ input: String, locale: Locale = .current) -> Double? {
        let raw = input.trimmingCharacters(in:.whitespacesAndNewlines)
        let separator = locale.decimalSeparator ?? "."
        let value = separator == "." ? raw : raw.replacingOccurrences(of:separator,with:".")
        guard !value.isEmpty, value.unicodeScalars.allSatisfy({ CharacterSet(charactersIn:"0123456789.").contains($0) }), value.filter({ $0 == "." }).count <= 1, let minutes = Double(value), minutes.isFinite, minutes >= 0, minutes <= 525_600 else { return nil }
        return minutes * 60
    }
}
