import Foundation

/// A cancelled or superseded system callback must never revive a queue.
public struct BackgroundQueueLease {
    public private(set) var identifier: UUID?
    public private(set) var granted = false
    public private(set) var total = 0
    public private(set) var completed = 0
    public init() {}
    public mutating func request(total: Int) -> UUID {
        let token = UUID(); identifier = token; granted = false
        self.total = max(1,total); completed = 0; return token
    }
    public mutating func grant(_ token: UUID) -> Bool {
        guard identifier == token, !granted else { return false }
        granted = true; return true
    }
    public mutating func addWork(_ count: Int) { if identifier != nil { total += max(0,count) } }
    public mutating func advance() {
        guard identifier != nil else { return }
        completed = min(total,completed + 1)
    }
    @discardableResult public mutating func finish(_ token: UUID) -> Bool {
        guard identifier == token else { return false }
        identifier = nil; granted = false; return true
    }
}

