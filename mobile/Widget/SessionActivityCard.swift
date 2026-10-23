import SwiftUI

/// The same card is rendered by the lock screen and offline layout previews.
struct SessionActivityCard<SpoofTimer: View, UnlockTimer: View>: View {
    let spoofActive: Bool
    let queueActive: Bool
    let stale: Bool
    let spoofGame: String
    let queueGame: String
    let nextAchievement: String
    let handled: Int
    let total: Int
    @ViewBuilder var spoofTimer: () -> SpoofTimer
    @ViewBuilder var unlockTimer: () -> UnlockTimer

    var body: some View {
        VStack(alignment:.leading,spacing:5) {
            Text("Achievement Labs").font(.system(size:13,weight:.semibold)).lineLimit(1)
            if stale {
                Label("Updates paused · open app to check",systemImage:"pause.circle")
                    .font(.system(size:12)).foregroundStyle(.secondary)
            } else {
                if spoofActive {
                    timerRow(title:"Spoofing",detail:spoofGame,timerTitle:"Elapsed",timer:spoofTimer)
                }
                if queueActive {
                    timerRow(title:"Auto Unlock · " + queueGame,detail:nextAchievement,timerTitle:"Next unlock",timer:unlockTimer)
                }
                if queueActive { Text("Queue \(handled)/\(total) processed").font(.system(size:10)).foregroundStyle(.secondary).lineLimit(1) }
            }
        }.padding(10).foregroundStyle(.white)
    }
    private func timerRow<Timer: View>(title: String,detail: String,timerTitle: String,@ViewBuilder timer: () -> Timer) -> some View {
        HStack(alignment:.center,spacing:10) {
            VStack(alignment:.leading,spacing:3) {
                Text(title).font(.system(size:10,weight:.medium)).foregroundStyle(.green).lineLimit(1)
                Text(detail).font(.system(size:13,weight:.semibold)).lineLimit(1).truncationMode(.tail)
            }.frame(maxWidth:.infinity,alignment:.leading)
            VStack(alignment:.trailing,spacing:2) {
                Text(timerTitle).font(.system(size:10,weight:.medium)).foregroundStyle(.secondary)
                timer().font(.system(size:19,weight:.semibold,design:.rounded)).monospacedDigit()
                    .lineLimit(1).minimumScaleFactor(0.7).multilineTextAlignment(.trailing)
                    .frame(maxWidth:.infinity,alignment:.trailing)
            }.frame(width:96,alignment:.trailing)
        }.activeSessionHighlight(true,radius:9,inset:5)
    }
}
