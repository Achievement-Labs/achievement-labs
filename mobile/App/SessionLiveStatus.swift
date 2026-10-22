import Foundation
import ActivityKit
@MainActor final class SessionLiveStatus {
    private var activity: Activity<LabsSessionAttributes>?
    private var pending: LabsSessionAttributes.ContentState?
    private var drain: Task<Void,Never>?
    private var generation = UUID()
    init() {
        for old in Activity<LabsSessionAttributes>.activities { Task { await old.end(nil,dismissalPolicy:.immediate) } }
    }
    func update(_ state: LabsSessionAttributes.ContentState, enabled: Bool) {
        guard enabled, ActivityAuthorizationInfo().areActivitiesEnabled else { end(); return }
        pending = state
        // One writer prevents a slow previous update from overwriting the next achievement.
        guard drain == nil else { return }
        let token = generation
        drain = Task { [weak self] in
            guard let self else { return }
            defer { if self.generation == token { self.drain = nil } }
            while self.generation == token, !Task.isCancelled, let latest = self.pending {
                self.pending = nil
                let content = ActivityContent(state:latest,staleDate:latest.validUntil,relevanceScore:1)
                if self.activity == nil { self.activity = try? Activity.request(attributes:LabsSessionAttributes(),content:content,pushType:nil) }
                if let current = self.activity { await current.update(content) }
            }
        }
    }
    func end() {
        generation = UUID(); pending = nil; drain?.cancel(); drain = nil
        guard let old = activity else { return }; activity = nil
        Task { await old.end(nil,dismissalPolicy:.immediate) }
    }
}
