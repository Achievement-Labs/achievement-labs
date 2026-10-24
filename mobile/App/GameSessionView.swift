import SwiftUI
import LabsMobileCore

struct QueueView: View {
    @EnvironmentObject var m:MobileModel
    @State private var settings=false
    private var game:Game? { m.gameForTitle(m.presenceTitle) ?? m.gameForTitle(m.queueTitle) ?? m.nextQueueItem?.game }
    var body:some View {
        NavigationStack {
            GeometryReader { window in
                ScrollView {
                    VStack(alignment:.leading,spacing:12) {
                        if let game { GameHeader(game:game).labsTile() }
                        else { VStack(alignment:.leading,spacing:8) { Text("Choose a game").font(.headline); Text("Open a game from Library, or enter a Title ID in Session Settings.").font(.subheadline).foregroundStyle(.secondary); Button("Session Settings") { settings=true } }.labsTile() }
                        LazyVGrid(columns:Array(repeating:GridItem(.flexible(),alignment:.top),count:window.size.width >= 650 && window.size.width > window.size.height ? 2 : 1),alignment:.leading,spacing:12) {
                            presenceTile
                            queueTile
                        }
                        VStack(alignment:.leading,spacing:10) {
                            HStack { Text("Queue").font(.headline);Spacer();Text("\(m.queue.count) achievements").font(.caption).foregroundStyle(.secondary) }
                            if m.queue.isEmpty { Text("Select achievements from Library, or load reference timing in Session Settings.").font(.subheadline).foregroundStyle(.secondary) }
                            ForEach(m.queue) { item in QueueRow(itemID:item.id); if item.id != m.queue.last?.id { Divider() } }
                        }.labsTile()
                    }.padding(14).frame(maxWidth:900).frame(maxWidth:.infinity)
                }.scrollDismissesKeyboard(.interactively)
            }.labsPage().navigationTitle("Game Session").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement:.topBarTrailing) { Button("Session Settings",systemImage:"slider.horizontal.3") { settings=true }.labelStyle(.titleAndIcon).font(.subheadline) } }
            .sheet(isPresented:$settings) { SessionSettingsView() }
            .onAppear {
                #if DEBUG
                if ProcessInfo.processInfo.arguments.contains("--ui-preview=session-settings") { settings=true }
                #endif
            }
            .task(id:m.presenceTitle) {
                do { try await Task.sleep(nanoseconds:400_000_000);try Task.checkCancellation();await m.lookupTitle(m.presenceTitle);await m.readPlaytime(m.presenceTitle) } catch {}
            }
            .task(id:m.queueTitle) { await m.lookupTitle(m.queueTitle) }
        }
    }
    private var presenceTile:some View {
        VStack(alignment:.leading,spacing:10) {
            HStack { Text("Spoofing").font(.headline);Spacer();Text(m.presenceRunning ? "Active" : "Stopped").font(.caption).foregroundStyle(m.presenceRunning ? Color.green : Color.secondary) }
            HStack(alignment:.top) {
                VStack(alignment:.leading,spacing:4) { Text("Session elapsed").font(.caption).foregroundStyle(.secondary);if let start=m.presenceStarted { Text(start,style:.timer).monospacedDigit().font(.title3) } else { Text("—").font(.title3) } }
                Spacer()
                VStack(alignment:.trailing,spacing:4) { Text("Xbox playtime").font(.caption).foregroundStyle(.secondary);Text(m.playedHours[m.presenceTitle].map { String(format:"%.2f h",$0) } ?? "—").font(.title3).monospacedDigit() }
            }
            Button(m.presenceRunning ? "Stop spoofing" : "Start spoofing",systemImage:m.presenceRunning ? "stop.fill" : "play.fill") {
                if m.presenceRunning { Task { await m.stopPresence() } } else { m.startPresence(m.presenceTitle) }
            }.buttonStyle(.bordered).frame(maxWidth:.infinity).disabled(m.presenceRunning ? false : m.account == nil || m.busy || !JSON.numeric(m.presenceTitle))
            Text(m.spoofStatus).font(.caption).foregroundStyle(.secondary)
            if m.continueSpoofInBackground || m.continueQueueInBackground { Text(m.extendedAudio.active ? m.extendedAudio.status : m.continuedQueue.status).font(.caption2).foregroundStyle(.secondary) }
        }.labsTile()
    }
    private var queueTile:some View {
        VStack(alignment:.leading,spacing:10) {
            HStack { Text("Auto unlock").font(.headline);Spacer();Text(m.running ? "Running" : "Paused").font(.caption).foregroundStyle(.secondary) }
            if let next=m.nextQueueItem { Text(next.achievement.name).font(.subheadline.weight(.semibold));Text(next.game.name).font(.caption).foregroundStyle(.secondary) }
            TimelineView(.periodic(from:.now,by:1)) { timeline in
                VStack(alignment:.leading,spacing:5) {
                    if let next=m.nextAction { HStack { Text("Next action");Spacer();Text(timerInterval:next.addingTimeInterval(-31_536_000)...next,countsDown:true).monospacedDigit().id(next) } }
                    let left=remaining(at:timeline.date)
                    if left > 0 { Text("Estimated finish: " + timeline.date.addingTimeInterval(left).formatted(date:.omitted,time:.shortened)).foregroundStyle(.secondary) }
                }.font(.caption)
            }
            Button(m.running ? "Pause queue" : "Start / Resume queue",systemImage:m.running ? "pause.fill" : "play.fill") { if m.running { m.pauseQueue() } else { m.startQueue() } }
                .buttonStyle(.borderedProminent).disabled(m.account == nil || m.busy || m.queue.isEmpty || m.editingQueueDelay)
            Text(m.status).font(.caption).foregroundStyle(.secondary)
            if m.continueQueueInBackground { Text(m.extendedAudio.active ? m.extendedAudio.status : m.continuedQueue.status).font(.caption2).foregroundStyle(.secondary) }
        }.labsTile()
    }
    private func remaining(at date:Date)->Double {
        var value=m.queue.filter { $0.status == "Waiting" }.reduce(0) { $0 + ($1.remainingDelay ?? $1.delay) }
        if let next=m.nextAction, let first=m.queue.first(where:{$0.status == "Waiting"}) { value=value-(first.remainingDelay ?? first.delay)+max(0,next.timeIntervalSince(date)) }
        return value
    }
}
struct SessionTitleSettings:View {
    @EnvironmentObject var m:MobileModel
    var body:some View {
        Section("Spoofing") {
            TextField("Title ID",text:$m.presenceTitle).keyboardType(.numbersAndPunctuation).disabled(m.presenceRunning)
            Button("Look up title") { Task { await m.lookupTitle(m.presenceTitle);await m.readPlaytime(m.presenceTitle) } }.disabled(m.busy || m.account == nil)
            Toggle("Notify if spoofing stops",isOn:$m.notifyPresenceFailure)
            Button("Refresh Xbox playtime") { Task { await m.readPlaytime(m.presenceTitle) } }.disabled(m.account == nil || !JSON.numeric(m.presenceTitle))
            if !m.spoofDiagnostics.isEmpty { DisclosureGroup("Diagnostics") { Text(m.spoofDiagnostics).font(.caption.monospaced()).textSelection(.enabled);Button("Copy diagnostics") { UIPasteboard.general.string=m.spoofDiagnostics } } }
        }.listRowBackground(LabsGlassSurface())
        Section("Queue tools") { Button("Remove completed items") { Task { await m.removeCompleted() } }.disabled(m.running || m.busy) }.listRowBackground(LabsGlassSurface())
    }
}
