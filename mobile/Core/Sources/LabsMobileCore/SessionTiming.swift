import Foundation

/// Timers are driven by absolute deadlines, never by a short display refresh lease.
public enum SessionTiming {
    public static func spoofExpiry(lastHeartbeat: Date?, started: Date?) -> Date? {
        (lastHeartbeat ?? started)?.addingTimeInterval(600)
    }
    public static func staleDate(now: Date, spoofExpiry: Date?, nextAction: Date?, queueRunning: Bool) -> Date {
        let queueCheck = queueRunning ? (nextAction ?? now).addingTimeInterval(120) : nil
        return [spoofExpiry,queueCheck].compactMap { $0 }.min() ?? now
    }
}
