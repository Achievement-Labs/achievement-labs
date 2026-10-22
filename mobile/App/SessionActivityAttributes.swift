import Foundation
import ActivityKit
struct LabsSessionAttributes: ActivityAttributes {
    struct ContentState: Codable, Hashable {
        var spoofStarted: Date?
        var queueHandled: Int
        var queueTotal: Int
        var queueRunning: Bool
        var nextAction: Date?
        var spoofGame: String?
        var queueGame: String?
        var nextAchievement: String?
        var updatedAt: Date?
        var validUntil: Date?
        var spoofExpires: Date?
        var queueItemID: String?
    }
}
