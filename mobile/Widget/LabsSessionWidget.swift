import SwiftUI
import WidgetKit
import ActivityKit

private struct SpoofClock: View {
    let state: LabsSessionAttributes.ContentState
    var body: some View {
        if let start = state.spoofStarted {
            Text(timerInterval:start...max(start,state.spoofExpires ?? start.addingTimeInterval(600)),countsDown:false).monospacedDigit()
        } else { Text("—") }
    }
}
private struct UnlockClock: View {
    let state: LabsSessionAttributes.ContentState
    var body: some View {
        if let next = state.nextAction, state.queueRunning {
            Text(timerInterval:next.addingTimeInterval(-31_536_000)...next,countsDown:true).monospacedDigit().id((state.queueItemID ?? "") + String(next.timeIntervalSince1970))
        } else { Text(state.queueRunning ? "…" : "—") }
    }
}
struct LabsSessionWidget: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for:LabsSessionAttributes.self) { context in
            SessionActivityCard(
                spoofActive:context.state.spoofStarted != nil,
                queueActive:context.state.queueRunning,
                stale:context.isStale,
                spoofGame:context.state.spoofGame ?? "Game",
                queueGame:context.state.queueGame ?? "Game",
                nextAchievement:context.state.nextAchievement ?? "Checking next achievement",
                handled:context.state.queueHandled,total:context.state.queueTotal,
                spoofTimer: { SpoofClock(state:context.state) },
                unlockTimer: { UnlockClock(state:context.state) }
            ).activityBackgroundTint(.black).activitySystemActionForegroundColor(.green)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    VStack(alignment:.leading,spacing:3) {
                        Text("Spoofing").font(.caption).foregroundStyle(.green)
                        Text(context.state.spoofGame ?? "—").font(.caption).lineLimit(1)
                        if !context.isStale { SpoofClock(state:context.state).font(.headline) }
                    }
                }
                DynamicIslandExpandedRegion(.trailing) {
                    VStack(alignment:.trailing,spacing:3) {
                        Text("Next unlock").font(.caption).foregroundStyle(.green)
                        Text(context.state.queueGame ?? "—").font(.caption2).lineLimit(1)
                        if !context.isStale { UnlockClock(state:context.state).font(.headline) }
                    }
                }
                DynamicIslandExpandedRegion(.bottom) {
                    if context.isStale { Text("Updates paused · open app to confirm status").font(.caption) }
                    else {
                        VStack(spacing:3) {
                            Text(context.state.queueRunning ? (context.state.nextAchievement ?? "Checking next achievement") : "Auto Unlock paused").font(.caption).lineLimit(2)
                            Text("Queue \(context.state.queueHandled)/\(context.state.queueTotal) processed · verify Xbox progress").font(.caption2)
                        }
                    }
                }
            } compactLeading: {
                if context.isStale { Image(systemName:"questionmark.circle").foregroundStyle(.orange) }
                else if context.state.spoofStarted != nil { HStack(spacing:3) { Image(systemName:"gamecontroller.fill"); SpoofClock(state:context.state).frame(width:48) }.font(.caption2).foregroundStyle(.green) }
                else { Image(systemName:"list.bullet").foregroundStyle(.green) }
            } compactTrailing: {
                if context.isStale { Text("Check").font(.caption2).foregroundStyle(.orange) }
                else if context.state.queueRunning { HStack(spacing:3) { Image(systemName:"hourglass"); UnlockClock(state:context.state).frame(width:48) }.font(.caption2) }
                else { SpoofClock(state:context.state).frame(width:54).font(.caption2) }
            } minimal: { Image(systemName:context.isStale ? "questionmark.circle" : "gamecontroller.fill").foregroundStyle(context.isStale ? .orange : .green) }
            .keylineTint(.green)
        }
    }
}


@main struct LabsWidgets: WidgetBundle {
    var body: some Widget { LabsSessionWidget() }
}

