import SwiftUI
import LabsMobileCore
import UniformTypeIdentifiers

@main struct AchievementLabsApp: App {
    @StateObject private var model = MobileModel()
    @StateObject private var appearance = MobileAppearance()
    var body: some Scene {
        WindowGroup { MobileRootView().environmentObject(model).environmentObject(appearance) }
    }
}
struct MobileRootView: View {
    @EnvironmentObject var model: MobileModel
    @EnvironmentObject var appearance: MobileAppearance
    @Environment(\.scenePhase) private var phase
    private var tabs: some View {
            TabView(selection: $model.selectedTab) {
                HomeView().tabItem { Label("Home",systemImage:"house") }.tag(4)
                LibraryView().tabItem { Label("Library", systemImage: "books.vertical") }.tag(0)
                QueueView().tabItem { Label("Game Session", systemImage: "gamecontroller") }.tag(1)
                Xbox360View().tabItem { Label("Xbox 360", systemImage: "externaldrive") }.tag(5)
                SettingsView().tabItem { Label("Settings", systemImage: "gear") }.tag(3)
            }
            .tint(appearance.accentColor).preferredColorScheme(.dark)
            .onAppear { if let title = model.selectedGame?.id ?? model.queue.first?.game.id, JSON.numeric(title) { appearance.title=title } }
            .onChange(of:model.presenceTitle) { _, value in if JSON.numeric(value) { appearance.title=value } }
            .onChange(of:model.queueTitle) { _, value in if JSON.numeric(value) { appearance.title=value } }
            .onChange(of:model.selectedGame?.id) { _, value in if let value { appearance.title=value } }

    }
    var body: some View {
        tabs
            .confirmationDialog("Add another game",isPresented:Binding(get:{ !model.pendingQueueItems.isEmpty },set:{ if !$0 { model.pendingQueueItems = [] } }),titleVisibility:.visible) {
                Button("Add to end of current queue") { model.resolveQueueAddition(separate:false) }
                Button("Save as a separate game queue") { model.resolveQueueAddition(separate:true) }
                Button("Cancel",role:.cancel) { model.pendingQueueItems = [] }
            } message: { Text("Add this game's achievements after the current items, or save it separately to load later.") }

            .safeAreaInset(edge: .bottom, spacing: 0) {
                if model.busy { ProgressView(model.status).font(.caption).padding(8).frame(maxWidth: .infinity).background(.ultraThinMaterial) }
            }
            .overlay {
                #if DEBUG
                if ProcessInfo.processInfo.arguments.contains("--web-callback-self-test") { CallbackCaptureTestView() }
                #endif
            }
            .alert("Achievement Labs", isPresented: Binding(get: { model.error != nil }, set: { if !$0 { model.error = nil } })) { Button("OK") { model.error = nil } } message: { Text(model.error ?? "") }
            .task {
                #if DEBUG
                if ProcessInfo.processInfo.arguments.contains("--audio-self-test") { await model.audioSelfTest(); return }
                if ProcessInfo.processInfo.arguments.contains("--collector-self-test") { await model.collectorSelfTest(); return }
                if ProcessInfo.processInfo.arguments.contains("--background-self-test") { await model.backgroundSelfTest(); return }
                #endif
                await model.maintainOnOpen()
            }
            .onOpenURL { url in
                guard url.scheme == "achievementlabs" else { return }
                if url.host == "queue" { model.selectedTab = 1; if url.path == "/pause" { model.pauseQueue() } }
                else if url.host == "spoof" { model.selectedTab = 1; if url.path == "/stop" { Task { await model.stopPresence() } } }
                else { model.selectedTab = 4 }
            }
            .onReceive(NotificationCenter.default.publisher(for:UIApplication.willTerminateNotification)) { _ in model.terminating() }
            .onChange(of: phase) { _, value in if value == .active { model.active() } else if value == .background { model.background() } }
    }
}


struct LibraryView: View {
    @EnvironmentObject var m: MobileModel
    @State private var search = ""
    @State private var findingGames = false
    @AppStorage("librarySort") private var sort = "Last played"
    var visible: [Game] {
        let filtered = m.games.filter { TitleSearch.matches($0.name,query:search) || $0.id.contains(search) }
        return filtered.sorted { a,b in
            if sort == "Last played", a.lastPlayed != b.lastPlayed { return a.lastPlayed > b.lastPlayed }
            let comparison = a.name.localizedStandardCompare(b.name)
            if comparison == .orderedSame { return a.id.localizedStandardCompare(b.id) == .orderedAscending }
            return sort == "Z–A" ? comparison == .orderedDescending : comparison == .orderedAscending
        }
    }
    var body: some View {
        NavigationStack {
            List {
                Section {
                    ProfileTile()
                    Picker("Sort", selection: $sort) { ForEach(["Last played", "A–Z", "Z–A"], id: \.self) { Text($0) } }.font(.subheadline)
                }.listRowBackground(LabsGlassSurface())
                Section {
                    ForEach(visible) { game in
                        NavigationLink(value:game) { LibraryGameRow(game:game) }
                            .listRowInsets(EdgeInsets(top:7,leading:14,bottom:7,trailing:14))
                            .listRowBackground(LabsGlassSurface(highlight:m.highlightCompleted && game.complete))
                            .swipeActions(edge:.trailing) {
                                Button("Game Session") { m.openGameSession(game) }.tint(.green)
                            }
                    }
                }.listRowBackground(LabsGlassSurface())
            }
            .labsPage().listSectionSpacing(.compact).navigationTitle("Library").navigationBarTitleDisplayMode(.inline)
            .navigationDestination(for: Game.self) { AchievementView(game: $0) }
            .searchable(text: $search, prompt: "Game name or Title ID")
            .refreshable { await m.refreshGames() }
            .toolbar {
                Button("Find any game") { findingGames = true }
                Button { Task { await m.refreshGames() } } label: { Image(systemName: "arrow.clockwise") }.disabled(m.account == nil || m.busy).accessibilityLabel("Refresh library")
            }
            .sheet(isPresented:$findingGames) { FindGamesView() }
            .onAppear {
                #if DEBUG
                if ProcessInfo.processInfo.arguments.contains("--ui-preview=lookup") { findingGames = true }
                #endif
            }
            .overlay { if m.games.isEmpty && !m.busy { ContentUnavailableView("Your Xbox library", systemImage: "gamecontroller", description: Text(m.account == nil ? "Open Settings to connect your account." : "Pull down or tap Refresh to load your games.")) } }
        }
    }
}

struct AchievementView: View {
    @EnvironmentObject var m: MobileModel
    let game: Game
    @State private var search = ""
    @State private var selected = Set<String>()
    @State private var filter = "All"
    @State private var sort = "Name"
    @State private var expanded: [String: Bool] = [:]
    var loaded: Bool { m.selectedGame?.id == game.id }
    var visible: [Achievement] {
        guard loaded else { return [] }
        return m.rows.filter { (search.isEmpty || $0.name.localizedCaseInsensitiveContains(search) || $0.detail.localizedCaseInsensitiveContains(search)) && (filter == "All" || ($0.unlocked ? filter == "Unlocked" : filter == "Locked")) }.sorted {
            sort == "ID" ? ($0.id.localizedStandardCompare($1.id) == .orderedAscending) : ($0.name.localizedStandardCompare($1.name) == .orderedAscending)
        }
    }
    var groupNames: [String] {
        let preferred = m.catalogue?.groups(game) ?? ["Achievements"]
        let actual = Set(visible.map { m.catalogue?.group($0, game: game) ?? "Achievements" })
        return preferred.filter(actual.contains)
    }
    var body: some View {
        List {
            Section {
                GameHeader(game:m.gameForTitle(game.id) ?? game)
                if let status = m.catalogue?.index[game.id] { Text("Event data: \(status.classification) · \(status.count) mobile mappings").font(.caption).foregroundStyle(.secondary) }
                HStack {
                    Button("Open Game Session") { if !selected.isEmpty { m.enqueue(m.rows.filter { selected.contains($0.id) }, game:game) }; m.openGameSession(game) }
                    Spacer()
                }.buttonStyle(.bordered).font(.caption)
            }.listRowBackground(LabsGlassSurface())
            Section {
                Picker("Show", selection: $filter) { ForEach(["All","Locked","Unlocked"],id: \.self) { Text($0) } }.pickerStyle(.segmented)
                Picker("Sort", selection: $sort) { Text("Name").tag("Name"); Text("ID").tag("ID") }
                HStack {
                    Button("Select visible") { for row in visible where m.unavailable(row, game: game) == nil { selected.insert(row.id) } }
                    Spacer(); Button("Clear") { selected = [] }
                }.font(.caption)
                Button("Unlock \(selected.count) selected together") { Task { await m.unlockTogether(m.rows.filter { selected.contains($0.id) },game:game) } }.disabled(selected.isEmpty || m.running || m.busy)
                Button("Add \(selected.count) to queue") { m.enqueue(m.rows.filter { selected.contains($0.id) }, game: game) }.disabled(selected.isEmpty || m.running)
            }.listRowBackground(LabsGlassSurface())
            ForEach(groupNames, id: \.self) { group in
                Section {
                    DisclosureGroup(isExpanded: Binding(get: { expanded[group] ?? true }, set: { expanded[group] = $0 })) {
                        ForEach(visible.filter { (m.catalogue?.group($0, game: game) ?? "Achievements") == group }) { row in
                            VStack(alignment: .leading,spacing: 8) {
                                HStack(alignment: .top) {
                                    Button { if selected.contains(row.id) { selected.remove(row.id) } else { selected.insert(row.id) } } label: { Image(systemName: selected.contains(row.id) ? "checkmark.square.fill" : "square").font(.title3) }.disabled(m.running || m.unavailable(row, game: game) != nil).accessibilityLabel("Select \(row.name)")
                                    VStack(alignment: .leading,spacing: 5) { Text(row.name).font(.headline); Text(row.detail).font(.caption).foregroundStyle(.secondary) }
                                    Spacer(minLength: 6)
                                    if row.unlocked { Image(systemName: "checkmark.circle.fill").foregroundStyle(.green).accessibilityLabel("Unlocked") }
                                }
                                HStack { Text("ID \(row.id) · \(row.score)G").font(.caption2).foregroundStyle(.secondary); Spacer(); Button(row.unlocked ? "Retry" : "Unlock") { Task { await m.unlock(row, game: game) } }.buttonStyle(.bordered).disabled(m.busy || m.running || m.unavailable(row, game: game) != nil) }
                                if let reason = m.unavailable(row, game: game) { Text(reason).font(.caption2).foregroundStyle(.secondary) }
                                if let result = m.rowResults[row.id] { Text(result).font(.caption).foregroundStyle(.orange) }
                            }.padding(.vertical,6)
                        }
                    } label: { Text(group).foregroundStyle(.green).font(.headline) }
                }.listRowBackground(LabsGlassSurface())
            }
        }
        .labsPage().navigationTitle("Achievements").navigationBarTitleDisplayMode(.inline)
        .searchable(text: $search, prompt: "Search achievements")
        .task(id: game.id) { await m.load(game) }
        .refreshable { await m.load(game) }
        .toolbar { Button { Task { await m.load(game) } } label: { Image(systemName: "arrow.clockwise") }.disabled(m.busy).accessibilityLabel("Refresh achievements") }
    }
}

struct SessionSettingsView: View {
    @Environment(\.dismiss) private var dismiss
    @EnvironmentObject var m: MobileModel
    @State private var options = false
    @State private var savedExpanded = false
    @State private var deletingSaved: SavedGameQueue?
    @State private var deletingOwner = ""
    @State private var exportQueues = false
    @State private var importQueues = false
    @State private var queueQR = false
    @State private var queueDocument = QueueJSONDocument(data:Data())
    var body: some View {
        NavigationStack {
            List {
                SessionTitleSettings()
                Section("Timing source") {
                    TextField("Reference gamertag",text:$m.referenceGamertag).textInputAutocapitalization(.never).autocorrectionDisabled().submitLabel(.go).onSubmit { build() }.disabled(m.running || m.busy)
                    TextField("Title ID",text:$m.queueTitle).keyboardType(.numbersAndPunctuation).submitLabel(.go).onSubmit { build() }.disabled(m.running || m.busy)
                    if let game = m.gameForTitle(m.queueTitle) { GameHeader(game:game) }
                    Button("Build timing queue",systemImage:"clock.arrow.circlepath") { build() }.disabled(m.running || m.busy || m.account == nil || m.referenceGamertag.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty || !JSON.numeric(m.queueTitle))
                    if !m.referenceInfo.isEmpty { Text(m.referenceInfo).font(.caption).foregroundStyle(.secondary) }
                    Text("Uses intervals between the reference profile’s visible unlocks. Review the queue before starting; it does not copy past dates.").font(.caption2).foregroundStyle(.secondary)
                }.listRowBackground(LabsGlassSurface())
                Section {
                    DisclosureGroup("Queue options",isExpanded:$options) {
                        Toggle("Stop on failure",isOn:$m.stopOnFailure)
                        Toggle("Notify on unlock failure",isOn:$m.notifyUnlockFailure)
                        Toggle("Stop if spoofing fails",isOn:$m.stopOnPresenceFailure)
                        Toggle("Notify on completion",isOn:$m.notifyCompletion)
                        Stepper("New item: \((m.queueDelay / 60).formatted()) min",value:Binding(get:{m.queueDelay / 60},set:{m.queueDelay=$0*60}),in:0...525600,step:1)
                        Button("Save queue delays") { m.saveQueue() }.disabled(m.running)
                        Text(m.continueQueueInBackground ? "Background continuation is experimental. iOS can pause it; long delays may expire. Refresh Xbox to verify progress." : "Queues pause when you leave the app. Refresh achievements to verify accepted requests.").font(.caption2).foregroundStyle(.secondary)
                    }
                }.listRowBackground(LabsGlassSurface()).font(.subheadline)
                Section {
                    DisclosureGroup("Saved game queues (\(m.queueBook.sorted.count))",isExpanded:$savedExpanded) {
                    HStack {
                        Button("Export queue sync file") { do { queueDocument = QueueJSONDocument(data:try m.exportQueueTransfer()); exportQueues = true } catch { m.error = error.localizedDescription } }
                        Spacer(); Button("Import queue sync file") { importQueues = true }
                    }.font(.caption).disabled(m.running || m.busy || m.account == nil)
                    Button("Queue QR export / scan",systemImage:"qrcode.viewfinder") { queueQR = true }.font(.caption).disabled(m.running || m.busy || m.account == nil)
                    if m.queueBook.sorted.isEmpty { Text("Queues are saved automatically for this account.").font(.caption).foregroundStyle(.secondary) }
                    ForEach(m.queueBook.sorted) { saved in
                        VStack(alignment:.leading,spacing:5) {
                            Text(saved.name).font(.subheadline.weight(.semibold))
                            Text("\(saved.items.count) achievements · saved \(saved.updated.formatted(date:.abbreviated,time:.shortened))").font(.caption2).foregroundStyle(.secondary)
                            HStack {
                                Button("Load separately") { m.loadGameQueue(saved.id,append:false) }
                                Spacer()
                                Button("Add to end of queue") { m.loadGameQueue(saved.id,append:true) }
                            }.font(.caption).buttonStyle(.borderless).disabled(m.running || m.busy)
                            Button("Delete saved queue",role:.destructive) { deletingOwner = m.account?.xuid ?? ""; deletingSaved = saved }.font(.caption).buttonStyle(.borderless).disabled(m.running || m.busy)
                        }
                    }
                    Text("To sync: Export queue sync file → copy the JSON file to your other device → Import queue sync file there while signed into the same account. This is a manual transfer; loading requires Start.").font(.caption2).foregroundStyle(.secondary)
                    }
                }.listRowBackground(LabsGlassSurface())
            }.scrollDismissesKeyboard(.interactively).listSectionSpacing(.compact).navigationTitle("Session Settings").navigationBarTitleDisplayMode(.inline).labsPage()
            .toolbar { ToolbarItem(placement:.confirmationAction) { Button("Done") { dismiss() } } }
            .confirmationDialog("Delete saved queue?",isPresented:Binding(get:{ deletingSaved != nil },set:{ if !$0 { deletingSaved = nil } }),titleVisibility:.visible) {
                if let saved = deletingSaved {
                    Button("Delete " + saved.name,role:.destructive) { if m.account?.xuid == deletingOwner { m.deleteSavedGameQueue(saved.id) }; deletingSaved = nil }
                }
                Button("Cancel",role:.cancel) { deletingSaved = nil }
            } message: { Text("This removes the saved game and its loaded queue items. Other games and Xbox achievements are unchanged.") }
            .sheet(isPresented:$queueQR) { QueueQRSyncView() }
            .fileExporter(isPresented:$exportQueues,document:queueDocument,contentType:.json,defaultFilename:"AchievementLabs-Queues") { result in if case .failure(let error) = result { m.error = error.localizedDescription } }
            .fileImporter(isPresented:$importQueues,allowedContentTypes:[.json]) { result in
                do { let url = try result.get(); let access = url.startAccessingSecurityScopedResource(); defer { if access { url.stopAccessingSecurityScopedResource() } }; let size = try url.resourceValues(forKeys:[.fileSizeKey]).fileSize ?? 0; guard size <= 8*1024*1024 else { throw LabsError.message("Queue file is too large.") }; let data = try Data(contentsOf:url); Task { await m.importQueueTransfer(data) } }
                catch { m.error = error.localizedDescription }
            }
            .task(id:m.queueTitle) { do { try await Task.sleep(nanoseconds:400_000_000); try Task.checkCancellation(); await m.lookupTitle(m.queueTitle) } catch {} }
        }
    }
    private func build() { Task { await m.buildTimingQueue() } }
    private func remaining(at date: Date) -> Double {
        var value = m.queue.filter { $0.status == "Waiting" }.reduce(0) { $0 + $1.delay }
        if let next = m.nextAction, let first = m.queue.first(where: { $0.status == "Waiting" }) { value = value - first.delay + max(0,next.timeIntervalSince(date)) }
        return value
    }
}
struct QueueRow: View {
    @EnvironmentObject var m: MobileModel
    let itemID: String
    @FocusState private var delayFocused: Bool
    @State private var delayText = ""
    private var item: QueueItem? { m.queue.first { $0.id == itemID } }
    private func loadDelay() { if let item { delayText = (item.delay / 60).formatted(.number.precision(.fractionLength(0...4)).grouping(.never)) } }
    private func confirmDelay() {
        guard item != nil, !m.running else { delayFocused = false; m.editingQueueDelay = false; return }
        guard let seconds = QueueDelayInput.seconds(delayText) else { m.error = "Enter a valid delay from 0 to 525600 minutes."; return }
        m.editQueueItem(itemID,seconds:seconds); delayFocused = false; m.editingQueueDelay = false
        UIApplication.shared.sendAction(#selector(UIResponder.resignFirstResponder),to:nil,from:nil,for:nil)
    }
    var body: some View {
        if let item {
            VStack(alignment:.leading,spacing:6) {
                HStack(spacing:8) {
                    GameArt(url:item.game.image,size:30)
                    VStack(alignment:.leading,spacing:2) { Text(item.achievement.name).font(.subheadline.weight(.semibold)); Text(item.game.name).font(.caption2).foregroundStyle(.secondary).lineLimit(1) }
                }
                Text(item.achievement.detail).font(.caption).foregroundStyle(.secondary)
                Text(item.status).font(.caption2).foregroundStyle(item.status == "Already unlocked" ? .green : .secondary)
                if let source = item.referenceGamertag, let date = item.referenceDate { Text(source + " · " + date.formatted(date:.abbreviated,time:.shortened)).font(.caption2).foregroundStyle(.secondary) }
                HStack {
                    Text("Delay (minutes)").font(.caption)
                    TextField("Minutes",text:$delayText).keyboardType(.decimalPad).submitLabel(.done).focused($delayFocused).onSubmit { confirmDelay() }.multilineTextAlignment(.trailing).disabled(m.running).accessibilityIdentifier("queue-delay-field")
                }
                HStack {
                    if QueuePolicy.needsReview(item.status) { Button("Retry") { m.resetQueueItem(itemID) }.disabled(m.running || m.busy); Button("Skip") { m.skipQueueItem(itemID) }.disabled(m.running || m.busy) }
                    else if item.status != "Waiting" { Button("Reset") { m.resetQueueItem(itemID) }.disabled(m.running || m.busy) }
                    Spacer(); Button("Remove",role:.destructive) { delayFocused = false; m.editingQueueDelay = false; m.removeQueueItem(itemID) }.disabled(m.running || m.busy)
                }.font(.caption).buttonStyle(.borderless)
            }.padding(.vertical,2)
            .onAppear(perform:loadDelay)
            .onChange(of:self.item?.delay) { _,_ in if !delayFocused { loadDelay() } }
            .onChange(of:delayFocused) { was,focused in
                m.editingQueueDelay = focused
                if was && !focused, self.item != nil, let seconds = QueueDelayInput.seconds(delayText), !m.running { m.editQueueItem(itemID,seconds:seconds) }
            }
            .onDisappear { if delayFocused { m.editingQueueDelay = false } }
            .toolbar { ToolbarItemGroup(placement:.keyboard) { if delayFocused { Spacer(); Button("Done",action:confirmDelay).fontWeight(.semibold).accessibilityIdentifier("queue-delay-done") } } }
        }
    }
}
struct PresenceSections: View {
    @EnvironmentObject var m: MobileModel
    var body: some View {
        Group {
                Section {
                    VStack(alignment:.leading,spacing:12) {
                    TextField("Title ID",text:$m.presenceTitle).keyboardType(.numbersAndPunctuation).submitLabel(.done).onSubmit { Task { await m.lookupTitle(m.presenceTitle) } }.disabled(m.presenceRunning)
                    if let game = m.gameForTitle(m.presenceTitle) { GameHeader(game:game) }
                    if let start = m.presenceStarted { LabeledContent("Session elapsed") { Text(start,style:.timer).monospacedDigit() } }
                    if let hours = m.playedHours[m.presenceTitle] { LabeledContent("Xbox-recorded playtime",value:String(format:"%.2f hours",hours)).font(.caption) }
                    HStack {
                        Button("Start spoofing",systemImage:"play.fill") { m.startPresence(m.presenceTitle) }.disabled(m.account == nil || m.presenceRunning || m.busy || !JSON.numeric(m.presenceTitle))
                        Spacer()
                        Button("Stop",systemImage:"stop.fill",role:.destructive) { Task { await m.stopPresence() } }.disabled(!m.presenceRunning)
                    }.buttonStyle(.borderless)
                    Text(m.spoofStatus).font(.caption).foregroundStyle(.secondary)
                    if m.extendedAudio.active { Text(m.extendedAudio.status).font(.caption).foregroundStyle(.green) }
                    if m.continueSpoofInBackground || m.continueQueueInBackground { Text(m.extendedAudio.active ? m.extendedAudio.status : m.continuedQueue.status).font(.caption2).foregroundStyle(.secondary) }
                    }.activeSessionHighlight(m.presenceRunning)
                }.listRowBackground(LabsGlassSurface())
                if !m.spoofDiagnostics.isEmpty {
                    Section("Spoofing diagnostics") {
                        Text(m.spoofDiagnostics).font(.caption.monospaced()).textSelection(.enabled)
                        Button("Copy diagnostics",systemImage:"doc.on.doc") { UIPasteboard.general.string = m.spoofDiagnostics }
                        Text("Includes status, profile choice and server codes. Authorization and event tokens are excluded.").font(.caption2).foregroundStyle(.secondary)
                    }.listRowBackground(LabsGlassSurface())
                }
                Section {
                    Toggle("Notify if spoofing stops",isOn:$m.notifyPresenceFailure)
                    Button("Refresh Xbox playtime") { Task { await m.readPlaytime(m.presenceTitle) } }.disabled(m.account == nil || !JSON.numeric(m.presenceTitle))
                    Text("Background spoofing can use a finite session when enabled in Settings → App. For system-task mode, wait for a grant before leaving; for extended mode, wait for “Extended audio active”. iOS or audio interruptions can stop it. Xbox-recorded playtime can update later.").font(.caption2).foregroundStyle(.secondary)
                }.listRowBackground(LabsGlassSurface()).font(.subheadline)
        }
            .task(id:m.presenceTitle) { do { try await Task.sleep(nanoseconds:400_000_000); try Task.checkCancellation(); await m.lookupTitle(m.presenceTitle); await m.readPlaytime(m.presenceTitle) } catch {} }
    }
}

struct SettingsView: View {
    @Environment(\.openURL) private var openURL
    @State private var signInURL = ""
    @State private var embeddedLogin: SignInPage?
    @State private var advanced = false
    @EnvironmentObject var m: MobileModel
    @State private var authorization = ""
    @State private var eventToken = ""
    @State private var reveal = false
    @State private var pcSync = false
    @State private var category: SettingsCategory = SettingsCategory.previewDefault
    var body: some View {
        NavigationStack {
            VStack(spacing:0) {
                Picker("Settings category",selection:$category) {
                    ForEach(SettingsCategory.allCases) { Text($0.rawValue).tag($0) }
                }.pickerStyle(.menu).frame(maxWidth:.infinity,alignment:.leading).padding(.horizontal).padding(.vertical,8)
                Form {
                    if category == .account { accountSettings }
                    SyncSettings(category:category)
                    if category == .library { librarySettings }
                    if category == .app { appSettings }
                    if category == .appearance { AppearanceSettings() }
                }
            }.navigationTitle("Settings").navigationBarTitleDisplayMode(.inline).labsPage()
            .toolbar { Button { pcSync = true } label: { Label("Scan QR",systemImage:"qrcode.viewfinder") }.disabled(m.busy || m.running || m.presenceRunning) }
            .sheet(isPresented:$pcSync) { PCSyncView(startScanning:true) }
            .sheet(item:$embeddedLogin) { page in
                EmbeddedSignIn(url:page.url, onComplete: { redirect in
                    embeddedLogin = nil
                    Task { await m.finishBrowserLogin(redirect) }
                }, onCancel: { embeddedLogin = nil; m.cancelBrowserLogin() }, remember:m.rememberSignIn)
                .interactiveDismissDisabled()
            }
            .onAppear { eventToken = m.account?.eventToken ?? "" }
            .onChange(of:m.account?.eventToken) { _,value in eventToken = value ?? "" }
            .onChange(of: m.account?.xuid) { _, _ in eventToken = m.account?.eventToken ?? "" }
        }
    }
    @ViewBuilder private var accountSettings: some View {
                Section("Xbox account") {
                    Text(m.account?.gamertag ?? "Not connected")
                    if let profile = m.account?.loginProfile { Text("Current session: " + profile).font(.caption).foregroundStyle(.secondary) }
                    Picker("Sign-in profile",selection:$m.loginProfile) { ForEach(LoginProfile.allCases) { Text($0.rawValue).tag($0.rawValue) } }.disabled(m.busy || m.running || m.presenceRunning)
                    Button("Scan QR codes",systemImage:"qrcode.viewfinder") { pcSync = true }.disabled(m.busy || m.running || m.presenceRunning)
                    Text("For title spoofing, use the PC’s full Sign-in + event token QR if browser sign-in returns a heartbeat 403. Event-token-only sync leaves account authorization unchanged.").font(.caption).foregroundStyle(.secondary)
                    Text("Profile changes apply when you sign in again.").font(.caption2).foregroundStyle(.secondary)
                    Toggle("Remember sign-in",isOn:$m.rememberSignIn).disabled(m.busy || m.refreshingSignIn || m.running || m.presenceRunning).onChange(of:m.rememberSignIn) { _,_ in Task { await m.updateRememberSignIn() } }
                    Picker("Sign-in browser",selection:$m.signInBrowser) { Text("In app").tag("In app"); Text("Safari").tag("Safari") }.disabled(m.busy || m.refreshingSignIn)
                    Button("Sign in with Microsoft",systemImage:"person.crop.circle.badge.checkmark") {
                        if let url = m.beginBrowserLogin() {
                            if m.signInBrowser == "Safari" { advanced = true; signInURL = ""; openURL(url) }
                            else { embeddedLogin = SignInPage(url:url) }
                        }
                    }.disabled(m.busy || m.refreshingSignIn || m.running || m.presenceRunning)
                    if !m.sessionStatus.isEmpty { Text(m.sessionStatus).font(.caption).foregroundStyle(.secondary) }
                    if m.account?.refreshToken != nil && m.rememberSignIn { Button("Renew remembered sign-in") { Task { await m.renewSignIn() } }.disabled(m.busy || m.refreshingSignIn || m.running || m.presenceRunning) }
                    Text("Remembering stores renewal credentials in this device’s Keychain, not your password. Sign in once after this update to enable renewal. For incorrect password suggestions in the webpage, try Safari or choose your Microsoft account from Passwords.").font(.caption2).foregroundStyle(.secondary)
                    DisclosureGroup("Safari / manual sign-in",isExpanded:$advanced) {
                    Button("Sign in using Safari") { if let url = m.beginBrowserLogin() { signInURL = ""; openURL(url) } }.disabled(m.busy || m.running || m.presenceRunning)
                    if m.browserPending {
                        Text("Sign in in the browser. On the final page, copy the entire address from the address bar, return here and paste it below. The final page may be blank. Never share this URL.").font(.caption)
                        SecureField("Paste final sign-in URL", text: $signInURL).textContentType(.oneTimeCode).textInputAutocapitalization(.never).autocorrectionDisabled()
                        Button("Finish browser sign-in") { let value = signInURL; signInURL = ""; Task { await m.finishBrowserLogin(value) } }.disabled(signInURL.isEmpty || m.busy)
                        Button("Cancel sign-in", role: .cancel) { signInURL = ""; m.cancelBrowserLogin() }.disabled(m.busy)
                    }
                    SecureField("Paste Xbox authorization",text: $authorization).textContentType(.oneTimeCode).textInputAutocapitalization(.never).autocorrectionDisabled()
                    Button("Connect account") { Task { await m.connect(authorization); authorization = "" } }.disabled(authorization.isEmpty || m.busy || m.running || m.presenceRunning)
                    Text("Use the full Xbox authorization starting XBL3.0 x=…;… . The account is checked with Xbox before saving. Manual authorization is an advanced alternative to browser sign-in. The event token below is separate from account authorization.").font(.caption).foregroundStyle(.secondary)
                    }
                    if m.account != nil { Button("Disconnect",role: .destructive) { Task { await m.disconnect() } }.disabled(m.busy) }
                }.listRowBackground(LabsGlassSurface())
                Section("Event token") {
                    if reveal { TextField("Paste event token",text: $eventToken).textInputAutocapitalization(.never).autocorrectionDisabled() } else { SecureField("Paste event token",text: $eventToken).textContentType(.oneTimeCode).textInputAutocapitalization(.never).autocorrectionDisabled() }
                    TimelineView(.periodic(from:Date(),by:60)) { context in Text(EventTokenLifetime.status(m.account,at:context.date)).font(.caption).foregroundStyle(.secondary) }
                    Text("Issued expiry is supplied by Xbox. A token can be rejected earlier; an unknown expiry does not mean it is valid.").font(.caption2).foregroundStyle(.secondary)
                    Toggle("Show token", isOn: $reveal)
                    Button("Save event token") { m.saveEventToken(eventToken); eventToken = m.account?.eventToken ?? "" }.disabled(m.account == nil || eventToken.isEmpty || m.running || m.busy)
                    Button("Remove event token",role: .destructive) { m.removeEventToken(); eventToken = "" }.disabled(m.account == nil || m.running || m.busy)
                    Button("Test & save mobile event token") { Task { await m.testMobileEventToken() } }.disabled(m.account == nil || m.busy || m.refreshingSignIn || m.running || m.presenceRunning)
                    Text(m.eventTokenProbeStatus.isEmpty ? "Experimental: uses this device’s Xbox iOS browser sign-in. Requests token issuance only; no achievement events. An existing token is retained on failure." : m.eventTokenProbeStatus).font(.caption).foregroundStyle(.secondary)
                    Text("Stored in the iOS Keychain for this device. Changing accounts clears the previous account's event token from the active session. Retrieval from other apps is not available.").font(.caption).foregroundStyle(.secondary)
                }.listRowBackground(LabsGlassSurface())
    }
    @ViewBuilder private var librarySettings: some View {
                Section("Appearance") { Toggle("Show Mythic for base completion", isOn: $m.showMythic); Toggle("Highlight 100% completed games", isOn: $m.highlightCompleted) }.listRowBackground(LabsGlassSurface())
                Section("Completion indicators") {
                    Text("Mythic is the same local base-completion marker as desktop. The profile count includes verified base completions and titles with every achievement unlocked; it can increase after scanning.").font(.caption).foregroundStyle(.secondary)
                    Text("Checked \(m.checkedCompletion.count) of \(m.games.count) titles").font(.caption)
                    Button("Scan unchecked titles, one at a time") { Task { await m.scanCompletion() } }.disabled(m.account == nil || m.busy || m.running || m.games.isEmpty)
                    if m.scanningCompletion { Button("Stop after current title",role:.destructive) { m.scanningCompletion = false } }
                    Text(m.status).font(.caption2).foregroundStyle(.secondary)
                }.listRowBackground(LabsGlassSurface())
    }
    @ViewBuilder private var appSettings: some View {
                Section("Background sessions · experimental") {
                    Toggle("Allow standalone background spoofing",isOn:$m.continueSpoofInBackground).disabled(m.running || m.presenceRunning).onChange(of:m.continueSpoofInBackground) { _,_ in m.updateBackgroundPreference() }
                    if m.continueSpoofInBackground {
                        Toggle("Extended background audio",isOn:$m.useExtendedAudio).disabled(m.running || m.presenceRunning)
                        Stepper("Spoof session limit: \(Int(m.spoofBackgroundMinutes)) min",value:$m.spoofBackgroundMinutes,in:5...120,step:5).disabled(m.running || m.presenceRunning)
                        Text("Extended mode loops silent audio and mixes with other media. It may use more battery. Calls or audio interruptions stop it; resume manually. Starts only when you explicitly start spoofing. Its Live Activity uses a system-rendered ticking timer.").font(.caption2).foregroundStyle(.secondary)
                    }
                    if m.continuedQueue.available || m.useExtendedAudio {
                        Toggle("Continue queue in background",isOn:$m.continueQueueInBackground).disabled(m.running || m.presenceRunning).onChange(of:m.continueQueueInBackground) { _,_ in m.updateBackgroundPreference() }
                        Toggle("Keep spoofing alongside queue",isOn:$m.continueSpoofWithQueue).disabled(m.running || m.presenceRunning || !m.continueQueueInBackground)
                    }
                    Text(m.extendedAudio.active ? m.extendedAudio.status : m.continuedQueue.status).font(.caption)
                    Text("System-task mode requires iOS 26 and a grant before leaving. Extended audio works only during your finite spoofing session. iOS can still stop either mode. Accepted actions need Xbox verification. Either session can start first; extended mode is shared once spoofing begins.").font(.caption2).foregroundStyle(.secondary)
                    Text("When background mode is enabled, remembered sign-in and queue files remain accessible after the first device unlock. They stay protected on this device.").font(.caption2).foregroundStyle(.secondary)
                }.listRowBackground(LabsGlassSurface())
                Section("Secure connections") {
                    Text("Xbox event collector: Windows · Microsoft root validated")
                    Button("Test secure connections") { Task { await m.checkSecureConnections() } }.disabled(m.running || m.presenceRunning || m.checkingConnections)
                    Text("Connection checks send no account credentials or achievement events. Certificate validation stays enabled. Xbox event templates use the Windows collector. Its Microsoft root is validated only for that exact host.").font(.caption2).foregroundStyle(.secondary)
                    if !m.transportDiagnostics.isEmpty {
                        Text(m.transportDiagnostics).font(.caption.monospaced()).textSelection(.enabled)
                        Button("Copy connection diagnostics") { UIPasteboard.general.string = m.transportDiagnostics }
                    }
                }.listRowBackground(LabsGlassSurface())
                Section("Notifications") { Button("Allow notifications") { Task { await m.requestNotifications() } } }.listRowBackground(LabsGlassSurface())
                Section("Mobile beta") {
                    Text("Standalone iPhone and iPad app · " + (Bundle.main.object(forInfoDictionaryKey:"CFBundleShortVersionString") as? String ?? "Unknown")).font(.headline)
                    Text("Bundled event data: fork v1.0.8-fork.1. Event actions use only deterministic recipes that passed mobile checks. Unsupported edition routing remains disabled. Live Xbox behaviour needs device testing. New event games sync from the public upstream feed; Windows-specific tools are not included.").font(.caption).foregroundStyle(.secondary)
                    Text("App identifier: " + (Bundle.main.bundleIdentifier ?? "Unknown")).font(.caption2).textSelection(.enabled)
                    Link("Project on GitHub",destination: URL(string:"https://github.com/Achievement-Labs/achievement-labs")!)
                }.listRowBackground(LabsGlassSurface())
    }
}
