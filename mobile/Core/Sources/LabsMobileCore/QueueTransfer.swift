import Foundation

public struct QueueTransfer: Codable {
    public var schemaVersion = 1
    public var kind = "AchievementLabsQueueTransfer"
    public var ownerXuid: String
    public var games: [TransferGame]
    public init(ownerXuid: String, games: [TransferGame]) { self.ownerXuid = ownerXuid; self.games = games }
    public func validate(owner: String) throws {
        guard schemaVersion == 1, kind == "AchievementLabsQueueTransfer", JSON.numeric(owner), ownerXuid == owner,
              games.count <= 500, Set(games.map(\.titleId)).count == games.count,
              games.reduce(0,{ $0 + $1.items.count }) <= 50000 else { throw LabsError.message("Queue transfer version, account or size does not match.") }
        for game in games {
            guard JSON.numeric(game.titleId), game.name.count <= 256, !game.items.isEmpty,
                  Set(game.items.map(\.achievementId)).count == game.items.count else { throw LabsError.message("Invalid game queue in transfer.") }
            for item in game.items {
                guard JSON.numeric(item.achievementId), item.name.count <= 256, item.status.count <= 512,
                      item.delaySeconds.isFinite, (0...31_536_000).contains(item.delaySeconds),
                      item.remainingDelaySeconds == nil || (item.remainingDelaySeconds!.isFinite && (0...31_536_000).contains(item.remainingDelaySeconds!)) else { throw LabsError.message("Invalid achievement or delay in transfer.") }
            }
        }
    }
    public static func importedStatus(_ status: String, unlocked: Bool) -> String {
        if unlocked { return "Already unlocked" }
        if status == "Waiting" { return "Waiting" }
        if status == "Skipped by user" { return status }
        return "Needs review — transferred progress; refresh before retrying"
    }
}
public struct TransferGame: Codable {
    public var titleId: String; public var name: String; public var items: [TransferAchievement]
    public init(_ saved: SavedGameQueue) {
        titleId = saved.id; name = saved.name
        items = saved.items.map { TransferAchievement(achievementId:$0.achievement.id,name:$0.achievement.name,delaySeconds:$0.delay,remainingDelaySeconds:$0.remainingDelay,status:$0.status) }
    }
}
public struct TransferAchievement: Codable {
    public var achievementId: String; public var name: String; public var delaySeconds: Double; public var remainingDelaySeconds: Double?; public var status: String
    public init(achievementId: String, name: String, delaySeconds: Double, remainingDelaySeconds: Double?, status: String) { self.achievementId = achievementId; self.name = name; self.delaySeconds = delaySeconds; self.remainingDelaySeconds = remainingDelaySeconds; self.status = status }
}
