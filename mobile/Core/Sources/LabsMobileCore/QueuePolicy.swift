import Foundation

public enum QueueDecision: Equatable { case send(Int), review(Int), done }
public enum QueuePolicy {
    public static func needsReview(_ status: String) -> Bool {
        ["Failed", "Interrupted", "Unconfirmed", "Needs review"].contains { status.hasPrefix($0) }
    }
    public static func terminal(_ status: String) -> Bool {
        ["Accepted", "Already unlocked", "Confirmed", "Skipped by user"].contains { status.hasPrefix($0) }
    }
    public static func next(_ items: [QueueItem], stopOnFailure: Bool) -> QueueDecision {
        for (index,item) in items.enumerated() {
            if needsReview(item.status), stopOnFailure { return .review(index) }
            if item.status == "Waiting" { return .send(index) }
        }
        return .done
    }
}
