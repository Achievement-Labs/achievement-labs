import SwiftUI
import UniformTypeIdentifiers
import LabsMobileCore

enum SettingsCategory: String, CaseIterable, Identifiable {
    case account = "Account", sync = "Sync", library = "Library", app = "App", appearance = "Appearance", guides = "Guides"
    var id: String { rawValue }
    static var previewDefault: SettingsCategory {
        #if DEBUG
        let args = ProcessInfo.processInfo.arguments
        if args.contains("--ui-preview=settings-sync") { return .sync }
        if args.contains("--ui-preview=settings-guides") { return .guides }
        if args.contains("--ui-preview=settings-library") { return .library }
        if args.contains("--ui-preview=settings-appearance") { return .appearance }
        if args.contains("--ui-preview=settings-app") { return .app }
        #endif
        return .account
    }
}
struct SyncSettings: View {
    @EnvironmentObject var m: MobileModel
    let category: SettingsCategory
    @State private var pcSync = false
    @State private var scanImmediately = false
    @State private var importingEvents = false
    var body: some View {
        Group {
            if category == .app {
            Section("Launch") {
                Picker("Open app to",selection:$m.launchPage) {
                    Text("Home / sign-in").tag(4); Text("Library").tag(0); Text("Game Session").tag(1); Text("Xbox 360").tag(5); Text("Settings").tag(3)
                }
            }.listRowBackground(LabsGlassSurface())
            Section("App updates") {
                Toggle("Check for updates on opening",isOn:$m.checkMobileUpdates)
                Toggle("Notify about new versions",isOn:$m.notifyMobileUpdates).onChange(of:m.notifyMobileUpdates) { _,enabled in if enabled { Task { await m.requestNotifications() } } }
                Button("Check for updates now") { Task { await m.checkForMobileUpdates() } }.disabled(m.updateChecking)
                Text(m.updateStatus).font(.caption).foregroundStyle(.secondary)
                if let update = m.availableUpdate { Link("Open iOS " + update.version + " download",destination:update.page) }
                Text("Checks run while the app is open. Download the IPA and sign/install through Signulous; this app cannot replace its own installed build.").font(.caption2).foregroundStyle(.secondary)
            }.listRowBackground(LabsGlassSurface())
            }
            if category == .library {
            Section("Library") { Toggle("Refresh on opening and sign-in",isOn:$m.autoRefreshLibrary) }.listRowBackground(LabsGlassSurface())
            }
            if category == .sync {
            Section("PC sync") {
                Button("Scan QR codes",systemImage:"qrcode.viewfinder") { scanImmediately = true; pcSync = true }.disabled(m.busy || m.running || m.presenceRunning)
                Button("PC sync / import file",systemImage:"desktopcomputer") { scanImmediately = false; pcSync = true }.disabled(m.busy || m.running || m.presenceRunning)
                Text("Click the mobile icon on PC. Select full sign-in, sign-in only, or event-token-only QR. Transfers expire after 10 minutes; encrypted file import remains available.").font(.caption2).foregroundStyle(.secondary)
            }.listRowBackground(LabsGlassSurface())
            Section("Event data sync") {
                Toggle("Sync new event games automatically",isOn:$m.autoSyncEvents)
                Button("Sync event data now") { Task { await m.syncEvents() } }.disabled(m.eventsSyncing || m.running)
                Button("Import event pack JSONâ€¦",systemImage:"square.and.arrow.down") { importingEvents = true }.disabled(m.eventsSyncing || m.running)
                Text(m.eventSyncStatus).font(.caption).foregroundStyle(.secondary)
                Text("The public feed checks the official repository hourly. Import a saved event pack to add new titles offline. New titles are marked Testing; existing mappings and edition fixes are retained.").font(.caption2).foregroundStyle(.secondary)
            }.listRowBackground(LabsGlassSurface())
            }
        }.sheet(isPresented:$pcSync) { PCSyncView(startScanning:scanImmediately) }
        .fileImporter(isPresented:$importingEvents,allowedContentTypes:[.json],allowsMultipleSelection:false) { result in
            do {
                guard let url = try result.get().first else { return }
                let access = url.startAccessingSecurityScopedResource(); defer { if access { url.stopAccessingSecurityScopedResource() } }
                guard try url.resourceValues(forKeys:[.fileSizeKey]).fileSize ?? 0 <= 32*1024*1024 else { throw LabsError.message("Event pack exceeds 32 MB.") }
                m.importEventPack(try Data(contentsOf:url))
            } catch { m.eventSyncStatus = "Could not read event pack. Existing data retained. " + error.localizedDescription }
        }
    }
}
struct PCSyncView: View {
    var startScanning = false
    @State private var startedScan = false
    @EnvironmentObject var m: MobileModel
    @Environment(\.dismiss) private var dismiss
    @State private var importing = false
    @State private var scanning = false
    @State private var file: Data?
    @State private var key = ""
    @State private var pendingQueue: Data?
    @State private var queueSummary = ""
    var body: some View {
        NavigationStack {
            Form {
                Section("QR sync") {
                    Button("Scan QR codes",systemImage:"qrcode.viewfinder") { scanning = true }.disabled(m.busy)
                    Text("Scan sign-in, event-token or saved queue codes from your own device. The scanner recognises the type automatically. Keep the looping codes in view.").font(.caption).foregroundStyle(.secondary)
                }.listRowBackground(LabsGlassSurface())
                Section("Encrypted file fallback") {
                    Button(file == nil ? "Choose .labslink file" : "Choose another file") { importing = true }
                    SecureField("Pairing key from PC",text:$key).textContentType(.oneTimeCode).textInputAutocapitalization(.never).autocorrectionDisabled()
                    Button("Verify PC account") {
                        guard let data = file else { return }; let value = key; file = nil; key = ""; pendingQueue = nil
                        Task { await m.preparePCTransfer(data,key:value) }
                    }.disabled(file == nil || key.isEmpty || m.busy)
                    Text(m.pcSyncStatus).font(.caption).foregroundStyle(.secondary)
                }.listRowBackground(LabsGlassSurface())
                if let data = pendingQueue {
                    Section("Saved game queues detected") {
                        Text(queueSummary).font(.headline)
                        Text("Matching saved queues will be replaced after Xbox verifies achievements. Nothing starts automatically.").font(.caption)
                        Button("Import saved queues") { pendingQueue = nil; m.error = nil; Task { await m.importQueueTransfer(data); m.pcSyncStatus = m.error ?? m.status } }.disabled(m.busy || m.running || m.presenceRunning)
                        Button("Cancel import",role:.cancel) { pendingQueue = nil }
                    }.listRowBackground(LabsGlassSurface())
                }
                if let imported = m.pendingPCAccount {
                    Section("Verified account") {
                        Text(imported.gamertag).font(.headline)
                        if !m.pendingPCEventOnly { Button("Connect this PC account") { m.usePCTransfer(eventOnly:false) } }
                        if !m.pendingPCSignInOnly, m.account?.xuid == imported.xuid, !imported.eventToken.isEmpty { Button("Import event token only") { m.usePCTransfer(eventOnly:true) } }
                        Text(m.pendingPCEventOnly ? "This QR contains only the event token. Your existing mobile sign-in and renewal stay in place." : "Connecting imports current authorization. Sign-in-only codes preserve an existing event token for the same account; full codes also import the event token. Microsoft renewal credentials remain on PC. Event-only import preserves your remembered mobile sign-in.").font(.caption2).foregroundStyle(.secondary)
                    }.listRowBackground(LabsGlassSurface())
                }
            }.disabled(m.running || m.presenceRunning)
            .navigationTitle("QR & PC sync").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement:.cancellationAction) { Button("Done") { m.pendingPCAccount = nil; key = ""; file = nil; dismiss() }.disabled(m.busy) } }
            .sheet(isPresented:$scanning) { PCQRScanner { data,key in
                scanning = false; pendingQueue = nil; m.pendingPCAccount = nil
                if key.isEmpty {
                    do {
                        guard !m.busy, !m.running, !m.presenceRunning else { throw LabsError.message("Pause sessions before importing saved queues.") }
                        guard let owner = m.account?.xuid else { throw LabsError.message("Sign in to the same Xbox account before importing saved queues.") }
                        let transfer = try JSONDecoder().decode(QueueTransfer.self,from:data); try transfer.validate(owner:owner)
                        pendingQueue = data; queueSummary = "\(transfer.games.count) games Â· \(transfer.games.reduce(0) { $0+$1.items.count }) achievements"; m.pcSyncStatus = "Saved game queue QR detected. Review before importing."
                    } catch { m.pcSyncStatus = error.localizedDescription }
                } else { Task { await m.preparePCTransfer(data,key:key) } }
            } }
            .fileImporter(isPresented:$importing,allowedContentTypes:[.item],allowsMultipleSelection:false) { result in
                do {
                    guard let url = try result.get().first else { return }
                    let access = url.startAccessingSecurityScopedResource(); defer { if access { url.stopAccessingSecurityScopedResource() } }
                    guard try url.resourceValues(forKeys:[.fileSizeKey]).fileSize ?? 0 <= 128*1024 else { throw LabsError.message("Transfer file is too large.") }
                    file = try Data(contentsOf:url); m.pcSyncStatus = "File selected. Enter its separate pairing key."
                } catch { m.pcSyncStatus = error.localizedDescription }
            }
        }.task {
            if startScanning && !startedScan {
                startedScan = true
                do { try await Task.sleep(nanoseconds:350_000_000); try Task.checkCancellation(); scanning = true } catch {}
            }
        }.onDisappear { key = ""; file = nil; pendingQueue = nil; m.pendingPCAccount = nil }
    }
}

