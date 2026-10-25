import SwiftUI
import LabsMobileCore
import UserNotifications
import UIKit
import WebKit
import Combine

final class NotificationPresentation: NSObject, UNUserNotificationCenterDelegate {
    static let shared = NotificationPresentation()
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification, withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) { completionHandler([.banner, .sound]) }
}

@MainActor final class MobileModel: ObservableObject {
    @Published private(set) var account: Account?
    @Published var games: [Game] = []
    @Published var rows: [Achievement] = []
    @Published var selectedGame: Game?
    @Published var queue: [QueueItem] = []
    @Published var queueBook = SavedQueueBook()
    @Published var pendingQueueItems: [QueueItem] = []
    private var pendingQueueReplacesTitle = false
    private var queueBookURL: URL? { account.map { directory.appendingPathComponent("queue-library-\($0.xuid).json") } }
    @Published var status = "Connect an Xbox account in Settings."
    @Published var busy = false
    @Published var running = false
    @Published var presenceTitle = ""
    @Published var presenceStarted: Date?
    @Published var presenceRunning = false
    @Published var nextAction: Date?
    @Published var rowResults: [String: String] = [:]
    @Published var baseCompleted = Set<String>()
    @Published var selectedTab = 0
    @Published var error: String?
    @AppStorage("queueDelay") var queueDelay = 60.0
    @AppStorage("stopOnFailure") var stopOnFailure = true
    @AppStorage("stopOnPresenceFailure") var stopOnPresenceFailure = true
    @AppStorage("notifyCompletion") var notifyCompletion = true
    @AppStorage("notifyUnlockFailure") var notifyUnlockFailure = true
    @Published var nextQueueItemID: String?
    @Published var lastHeartbeat: Date?
    @AppStorage("notifyPresenceFailure") var notifyPresenceFailure = true
    @AppStorage("showMythic") var showMythic = true
    @AppStorage("highlightCompleted") var highlightCompleted = true
    @Published var queueTitle = ""
    @Published var referenceGamertag = ""
    @Published var referenceInfo = ""
    @Published var eventsSyncing = false
    @Published var eventSyncStatus = "Bundled catalogue loaded."
    @Published var updateChecking = false
    @Published var updateStatus = "No update check yet."
    @Published var availableUpdate: MobileRelease?
    @Published var pendingPCSignInOnly = false
    @Published var pendingPCEventOnly = false
    @Published var pendingPCAccount: Account?
    private var pendingPCExpiry: Date?
    @Published var pcSyncStatus = "Import an encrypted transfer exported from the PC app."
    @AppStorage("autoSyncEvents") var autoSyncEvents = true
    @AppStorage("checkMobileUpdates") var checkMobileUpdates = true
    @AppStorage("notifyMobileUpdates") var notifyMobileUpdates = true
    @AppStorage("autoRefreshLibrary") var autoRefreshLibrary = true
    private var lifecycleRunning = false
    private var lastLibraryRefresh = Date.distantPast
    private var lastEventsCheck = Date.distantPast
    private var lastUpdateCheck = Date.distantPast
    @Published var refreshingSignIn = false
    @Published var sessionStatus = ""
    @Published var eventTokenProbeStatus = ""
    @Published var spoofDiagnostics = ""
    @AppStorage("launchPage") var launchPage = 4
    @AppStorage("rememberSignIn") var rememberSignIn = true
    @AppStorage("signInBrowser") var signInBrowser = "In app"
    private var renewalTask: Task<Account,Error>?
    @Published var spoofStatus = "No heartbeat sent."
    @Published var playedHours: [String:Double] = [:]
    @Published var titleDetails: [String:Game] = [:]
    @Published var checkedCompletion = Set<String>()
    @Published var scanningCompletion = false
    @AppStorage("loginProfile") var loginProfile = LoginProfile.desktop.rawValue
    private var savedTotals: [String:Int] = [:]
    let api: XboxService = {
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains(where: { $0.hasPrefix("--ui-preview=") }) {
            let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [PreviewOfflineProtocol.self]
            return XboxService(session:URLSession(configuration:config))
        }
        #endif
        return XboxService()
    }()
    let catalogue: Catalogue?
    private var generation = UUID()
    @AppStorage("continueQueueInBackground") var continueQueueInBackground = false
    @Published var transportDiagnostics = ""
    @Published var checkingConnections = false
    @Published var editingQueueDelay = false
    @AppStorage("continueSpoofInBackground") var continueSpoofInBackground = false
    @AppStorage("spoofBackgroundMinutes") var spoofBackgroundMinutes = 30.0
    @Published var spoofStopReason = ""
    @Published var spoofCleanupStatus = ""
    private var presenceDeadline: Date?
    private var sessionStatusTask: Task<Void,Never>?
    private var sessionStatusGeneration = UUID()
    private var backgroundHadFailure = false
    @AppStorage("continueSpoofWithQueue") var continueSpoofWithQueue = true
    let continuedQueue = ContinuedQueue()
    private var continuedObservation: AnyCancellable?
    private var audioObservation: AnyCancellable?
    @AppStorage("useExtendedAudio") var useExtendedAudio = false
    let extendedAudio = ExtendedAudioSession()
    let liveStatus = SessionLiveStatus()
    var extendedBackgroundAllowed: Bool { useExtendedAudio && extendedAudio.active && (presenceDeadline ?? .distantPast) > Date() }
    var queueBackgroundAllowed: Bool { continueQueueInBackground && running && queueTask?.isCancelled != true && (continuedQueue.active || extendedBackgroundAllowed) }
    var presenceBackgroundAllowed: Bool { presenceRunning && presenceTask?.isCancelled != true && (extendedBackgroundAllowed || (continuedQueue.active && ((continueSpoofInBackground && (presenceDeadline ?? .distantPast) > Date()) || (continueSpoofWithQueue && queueBackgroundAllowed)))) }
    private var queueTask: Task<Void, Never>?
    private var presenceTask: Task<Void, Never>?
    private var foreground = true
    private var presenceStopping = false
    private var presenceCleanupNeeded = false
    private let directory: URL
    private var queueURL: URL? { account.map { directory.appendingPathComponent("queue-\($0.xuid).json") } }
    init() {
        UNUserNotificationCenter.current().delegate = NotificationPresentation.shared
        directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("AchievementLabs", isDirectory: true)
        catalogue = Bundle.main.resourceURL.map { try? Catalogue(folder: $0.appendingPathComponent("Catalogue")) } ?? nil
        continuedObservation = continuedQueue.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send(); Task { @MainActor [weak self] in self?.updateSessionCard() } }
        audioObservation = extendedAudio.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send(); Task { @MainActor [weak self] in self?.updateSessionCard() } }
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains("--storage-self-test") {
            do {
                guard try SecureStore.read() == nil else { throw LabsError.message("Synthetic storage check requires an empty simulator Keychain.") }
                let fixture = Data("synthetic-storage-fixture".utf8)
                try SecureStore.save(fixture)
                guard try SecureStore.read() == fixture else { throw LabsError.message("Synthetic storage read did not match.") }
                try SecureStore.remove()
                guard try SecureStore.read() == nil else { throw LabsError.message("Synthetic storage delete failed.") }
            } catch { fatalError("Synthetic Keychain check failed: \(error.localizedDescription)") }
        }
        #endif
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            if !rememberSignIn { try SecureStore.remove() }
            if rememberSignIn, let data = try SecureStore.read() { account = try JSONDecoder().decode(Account.self, from: data); status = "Saved account loaded. Refresh the library to verify authorization."; loadQueue(); loadCompletion() }
        } catch { self.error = "Could not load protected account or queue storage." }
        if catalogue == nil { error = "Bundled catalogue could not be read. Event actions remain unavailable." }
        else if FileManager.default.fileExists(atPath:directory.appendingPathComponent("synced-events.json").path) {
            do { try catalogue?.loadCache(directory.appendingPathComponent("synced-events.json")); eventSyncStatus = "Cached synced event data loaded." }
            catch { eventSyncStatus = "Synced cache could not be validated. Bundled data retained." }
        }
        selectedTab = account == nil ? 4 : ([0,1,2,3,4,5].contains(launchPage) ? (launchPage == 2 ? 1 : launchPage) : 4)
        #if DEBUG
        if let argument = ProcessInfo.processInfo.arguments.first(where: { $0.hasPrefix("--ui-preview=") }) { configurePreview(String(argument.dropFirst("--ui-preview=".count))) }
        #endif
    }
    private func saveAccount(_ input: Account) throws {
        var next = input
        if let previous=account,previous.xuid == next.xuid {
            if previous.eventToken == next.eventToken && next.eventTokenExpiryHash == nil { next.eventTokenExpiresAt=previous.eventTokenExpiresAt;next.eventTokenExpiryHash=previous.eventTokenExpiryHash }
        }
        if EventTokenLifetime.expiry(next) == nil { next.eventTokenExpiresAt=nil;next.eventTokenExpiryHash=nil }
        if rememberSignIn { try SecureStore.save(JSONEncoder().encode(next),allowBackground:continueQueueInBackground || continueSpoofInBackground) }
        else { next.refreshToken = nil; try SecureStore.remove() }
        if account?.xuid != next.xuid { eventTokenProbeStatus = "" }
        account = next
    }
    private func authorizedAccount(force: Bool = false) async throws -> Account {
        if let renewalTask { return try await renewalTask.value }
        guard let current = account else { throw LabsError.message("Connect your Xbox account first.") }
        guard force || current.needsRefresh() else { return current }
        guard rememberSignIn, current.refreshToken?.isEmpty == false, let profile = current.loginProfile.flatMap(LoginProfile.init(rawValue:)) else {
            if force || current.needsRefresh() { throw LabsError.message("Sign in once with Remember sign-in enabled to renew this session.") }; return current
        }
        let version = generation; refreshingSignIn = true; sessionStatus = "Renewing remembered sign-in…"
        let task = Task { @MainActor [self] () async throws -> Account in
            defer { renewalTask = nil; refreshingSignIn = false }
            do {
                let next = try await BrowserLogin(profile:profile).refresh(current) { rotated in
                    guard version == self.generation, self.account?.xuid == current.xuid, self.rememberSignIn else { throw CancellationError() }
                    var saved = rotated; saved.eventToken = self.account?.eventToken ?? rotated.eventToken
                    try self.saveAccount(saved)
                }
                guard version == generation, account?.xuid == current.xuid else { throw CancellationError() }
                var saved = next; saved.eventToken = account?.eventToken ?? next.eventToken
                try saveAccount(saved); sessionStatus = "Remembered sign-in renewed."; return saved
            } catch {
                sessionStatus = "Could not renew sign-in. A browser sign-in may be needed."; throw error
            }
        }
        renewalTask = task
        return try await task.value
    }
    /// Only read operations may be repeated after a 401; unlocks and failed heartbeats are never replayed here.
    func openGameSession(_ game:Game) {
        titleDetails[game.id] = game
        if !running && !presenceRunning && !busy {
            if queueBook.shouldRestore(game.id, current:queue, active:running || presenceRunning) {
                loadGameQueue(game.id, append:false)
            }
            queueTitle = game.id; presenceTitle = game.id
        }
        else if queueTitle != game.id || presenceTitle != game.id { status = "Stop active sessions before changing games." }
        selectedTab = 1
    }
    private func readWithAccount<T>(_ operation: (Account) async throws -> T) async throws -> T {
        let current = try await authorizedAccount()
        do { return try await operation(current) }
        catch let error as XboxHTTPFailure where error.status == 401 && current.refreshToken?.isEmpty == false && rememberSignIn {
            let refreshed = try await authorizedAccount(force:true)
            return try await operation(refreshed)
        }
    }
    func renewSignIn() async {
        guard !busy, !running, !presenceRunning else { return }; busy = true; defer { busy = false }
        do { _ = try await authorizedAccount(force:true); status = "Sign-in renewed. No achievement or heartbeat request was sent." }
        catch { self.error = error.localizedDescription }
    }
    func updateRememberSignIn() async {
        guard !refreshingSignIn else { return }
        do {
            if let current = account { try saveAccount(current) } else if !rememberSignIn { try SecureStore.remove() }
            if !rememberSignIn { await clearMicrosoftCookies(); sessionStatus = "Sign-in will be forgotten when the app closes." }
            else { sessionStatus = account?.refreshToken == nil ? "Sign in once to enable automatic renewal." : "Sign-in is remembered on this device." }
        } catch { self.error = error.localizedDescription }
    }
    private func clearMicrosoftCookies() async {
        let store = WKWebsiteDataStore.default(); let types = WKWebsiteDataStore.allWebsiteDataTypes()
        let records = await store.dataRecords(ofTypes:types)
        let microsoft = records.filter { record in
            ["live.com","microsoft.com","microsoftonline.com","msauth.net","msftauth.net"].contains { record.displayName == $0 || record.displayName.hasSuffix("." + $0) }
        }
        await store.removeData(ofTypes:types,for:microsoft)
    }
    func connect(_ token: String) async {
        guard !busy, !refreshingSignIn, !running, !presenceRunning else { return }; busy = true; defer { busy = false }
        browserLogin = nil; browserPending = false
        let version = generation
        do {
            var next = try await api.connect(token)
            guard version == generation else { return }
            if next.xuid == account?.xuid { next.eventToken = account?.eventToken ?? "" }
            try saveAccount(next); sessionStatus = rememberSignIn && next.refreshToken != nil ? "Sign-in remembered with automatic renewal." : "Session saved; browser sign-in may be needed when it expires."; generation = UUID(); games = []; rows = []; selectedGame = nil; baseCompleted = []; rowResults = [:]; queue = []; titleDetails = [:]; playedHours = [:]; referenceInfo = ""; loadQueue(); loadCompletion()
            status = "Connected as \(next.gamertag)."
            Task { await maintainOnOpen(forceLibrary:true) }
        } catch { self.error = error.localizedDescription }
    }
    @Published var browserPending = false
    private var browserLogin: BrowserLogin?
    func beginBrowserLogin() -> URL? {
        guard !busy, !refreshingSignIn, !running, !presenceRunning else { return nil }
        let login = BrowserLogin(profile:LoginProfile(rawValue:loginProfile) ?? .desktop); browserLogin = login; browserPending = true
        return login.startURL
    }
    func cancelBrowserLogin() { guard !busy else { return }; browserLogin = nil; browserPending = false }
    func finishBrowserLogin(_ redirect: String) async {
        guard let login = browserLogin, !busy, !refreshingSignIn, !running, !presenceRunning else { return }
        busy = true; defer { busy = false; browserPending = false; browserLogin = nil; Task { await maintainOnOpen(forceLibrary:true) } }
        do {
            var next = try await login.complete(redirect)
            if next.xuid == account?.xuid { next.eventToken = account?.eventToken ?? "" }
            try saveAccount(next); sessionStatus = rememberSignIn && next.refreshToken != nil ? "Sign-in remembered with automatic renewal." : "Session saved; browser sign-in may be needed when it expires."; generation = UUID(); games = []; rows = []; selectedGame = nil; baseCompleted = []; rowResults = [:]; queue = []; titleDetails = [:]; playedHours = [:]; referenceInfo = ""; loadQueue(); loadCompletion()
            status = "Connected as \(next.gamertag). Refresh the library."
        } catch { self.error = error.localizedDescription }
    }
    func testMobileEventToken() async {
        guard !busy, !refreshingSignIn, !running, !presenceRunning, let current = account else { return }
        guard rememberSignIn, current.loginProfile == LoginProfile.ios.rawValue, current.refreshToken?.isEmpty == false else {
            eventTokenProbeStatus = "Sign in with Xbox iOS and Remember sign-in enabled on this device first. PC QR/manual sessions cannot run this test."; return
        }
        busy = true; refreshingSignIn = true; defer { busy = false }
        eventTokenProbeStatus = "Requesting mobile event-token authorization… No achievement events are sent."
        let version = generation
        let task = Task { @MainActor [self] () async throws -> Account in
            defer { renewalTask = nil; refreshingSignIn = false }
            let next = try await BrowserLogin(profile:.ios).acquireEventToken(current) { rotated in
                guard version == self.generation, self.account?.xuid == current.xuid, self.rememberSignIn else { throw CancellationError() }
                var saved = rotated; saved.eventToken = self.account?.eventToken ?? rotated.eventToken
                try self.saveAccount(saved)
            }
            guard version == generation, account?.xuid == current.xuid, rememberSignIn else { throw CancellationError() }
            try saveAccount(next); return next
        }
        renewalTask = task
        do { _ = try await task.value; eventTokenProbeStatus = "Microsoft issued a matching event token; saved securely. No achievement events were sent. Collector acceptance is not tested." }
        catch is CancellationError { eventTokenProbeStatus = "Test cancelled. No event token was replaced." }
        catch let problem as LabsError { eventTokenProbeStatus = (problem.errorDescription ?? "Authorization failed.") + " Existing event token retained." }
        catch { eventTokenProbeStatus = "Mobile event-token test could not complete. Existing event token retained. No achievement events were sent." }
    }
    func saveEventToken(_ input: String) {
        guard var current = account, !running, !busy else { return }
        do { current.eventToken = try Tokens.normalize(input, event: true); try saveAccount(current); status = "Event token saved in this device's Keychain." }
        catch { self.error = error.localizedDescription }
    }
    func removeEventToken() { guard var current = account, !running, !busy else { return }; current.eventToken = ""; do { try saveAccount(current) } catch { self.error = error.localizedDescription } }
    func disconnect() async {
        guard !busy else { return }
        busy = true; defer { busy = false }
        browserLogin = nil; browserPending = false; generation = UUID(); renewalTask?.cancel()
        pauseQueue(); if let queueTask { await queueTask.value }; await stopPresence(); generation = UUID()
        do { try SecureStore.remove(); await clearMicrosoftCookies(); account = nil; games = []; rows = []; queue = []; queueBook = SavedQueueBook(); pendingQueueItems = []; selectedGame = nil; baseCompleted = []; checkedCompletion = []; savedTotals = [:]; titleDetails = [:]; playedHours = [:]; status = "Disconnected." }
        catch { self.error = "Could not remove the Keychain session." }
    }
    func refreshGames() async {
        guard let account, !busy else { return }; busy = true; defer { busy = false }; let version = generation
        status = "Downloading library…"
        do { let loaded = try await readWithAccount { try await api.games($0) }; guard version == generation else { return }; games = loaded.map(withTotal)
            do { let credentials = self.account ?? account
                var profile = try await api.connect(credentials.authorization)
                guard profile.xuid == credentials.xuid else { throw LabsError.message("Profile identity changed.") }
                profile.eventToken = credentials.eventToken; profile.loginProfile = credentials.loginProfile; profile.refreshToken = credentials.refreshToken; profile.authorizationExpiresAt = credentials.authorizationExpiresAt; try saveAccount(profile) }
            catch { status = "Library loaded; profile details could not refresh." }
            lastLibraryRefresh = Date(); status = "Loaded \(loaded.count) titles." }
        catch { self.error = error.localizedDescription; status = "Refresh failed; previous library retained." }
    }
    func load(_ game: Game) async {
        guard let account, !busy else { return }; busy = true; defer { busy = false }; let version = generation
        do {
            var loaded = try await readWithAccount { try await api.achievements(game,account:$0) }
            if loaded.contains(where: \.eventBased) { for i in loaded.indices { loaded[i].eventBased = true } }
            guard version == generation else { return }
            rows = loaded; selectedGame = game; rowResults = [:]
            if !games.contains(where:{ $0.id == game.id }) {
                let found = TitleSearch.applyingProgress(game,rows:loaded)
                titleDetails[game.id] = found; selectedGame = found
            }
            rememberCompletion(loaded, game:game)
            // Never overwrite valid legacy/library progress with an empty response.
            if !loaded.isEmpty, (!game.legacy || loaded.filter(\.unlocked).count >= game.unlocked), let i = games.firstIndex(where: { $0.id == game.id }) { games[i].unlocked = loaded.filter(\.unlocked).count; games[i].total = loaded.count }
            status = "Loaded \(loaded.count) achievements."
        } catch { self.error = error.localizedDescription }
    }
    func unavailable(_ row: Achievement, game: Game) -> String? {
        if game.legacy { return "Xbox 360/GFWL achievements are read-only on mobile." }
        if row.eventBased {
            guard let catalogue else { return "Event catalogue unavailable." }
            do { guard try catalogue.recipes(game.id).recipes[row.id] != nil else { return "No mapped mobile event data." } }
            catch { return error.localizedDescription }
            if account?.eventToken.isEmpty != false { return "Save a current event token in Settings." }
        } else if UUID(uuidString: row.serviceConfigID) == nil { return "Missing service configuration." }
        return nil
    }
    private func send(_ row: Achievement, game: Game, account: Account, allowBackground: Bool = false) async throws {
        guard foreground || (allowBackground && queueBackgroundAllowed) else { throw CancellationError() }
        if let reason = unavailable(row, game: game) { throw LabsError.message(reason) }
        if row.eventBased {
            guard let catalogue else { throw LabsError.message("Catalogue unavailable.") }
            let bodies = try catalogue.payloads(title: game.id, achievement: row.id, xuid: account.xuid)
            for body in bodies { try Task.checkCancellation(); guard foreground || (allowBackground && queueBackgroundAllowed) else { throw CancellationError() }; try await api.sendEvent(body, account: account,route:.windows) }
        } else { try await api.titleUnlock(row, game: game, account: account) }
    }
    func unlock(_ row: Achievement, game: Game) async { await unlockTogether([row],game:game) }
    func unlockTogether(_ selected: [Achievement], game: Game) async {
        guard account != nil, !busy, !running, foreground, !selected.isEmpty else { return }
        busy = true; defer { busy = false }
        do {
            let active = try await authorizedAccount()
            let eligible = selected.filter { unavailable($0,game:game) == nil }
            // Concurrent achievements, but ordered event recipes within each achievement.
            for start in stride(from:0,to:eligible.count,by:3) {
                let chunk = Array(eligible[start..<min(start+3,eligible.count)])
                await withTaskGroup(of:(String,String).self) { group in
                    for row in chunk { group.addTask { [self] in
                        do { try await self.send(row,game:game,account:active); return (row.id,"Request accepted; refresh to verify progress.") }
                        catch { return (row.id,"Request not confirmed: " + error.localizedDescription + ". Refresh progress before retrying.") }
                    } }
                    for await (id,result) in group { rowResults[id] = result }
                }
            }
            status = "Finished selected actions. Refresh Xbox to verify progress."
        } catch { self.error = error.localizedDescription }
    }
    var nextQueueItem: QueueItem? { nextQueueItemID.flatMap { id in queue.first { $0.id == id } } }
    private func uncertain(_ error: Error) -> Bool {
        if error is XboxTransportFailure { return true }
        if let http = error as? XboxHTTPFailure { return http.status >= 500 || http.status == 408 }
        return (error as NSError).domain == NSURLErrorDomain
    }
    private func failQueue(_ index: Int, error: Error, mayHaveSent: Bool) async {
        rememberTransport(error); nextAction = nil
        let item = queue[index]
        if mayHaveSent && uncertain(error) {
            if let rows = try? await readWithAccount({ try await api.achievements(item.game,account:$0) }), rows.contains(where:{ $0.id == item.achievement.id && $0.unlocked }) {
                queue[index].status = "Confirmed unlocked after connection interruption"; _ = saveQueue(); return
            }
            queue[index].status = "Unconfirmed — connection interrupted; refresh before retrying"
        } else { queue[index].status = "Failed: " + error.localizedDescription }
        backgroundHadFailure = true; _ = saveQueue(); status = "Auto Unlock stopped at " + item.achievement.name + ": " + queue[index].status
        if notifyUnlockFailure { await notify("Auto Unlock needs attention",body:item.game.name + " · " + item.achievement.name + ". Open the app to review; no automatic retry was sent.") }
    }
    func skipQueueItem(_ id: String) {
        guard !running, !busy, let i = queue.firstIndex(where:{ $0.id == id }) else { return }
        queue[i].status = "Skipped by user"; queue[i].remainingDelay = nil; _ = saveQueue()
    }
    func exportQueueTransfer() throws -> Data {
        guard let account, !running, !busy else { throw LabsError.message("Pause Auto Unlock before exporting queues.") }
        guard saveQueue() else { throw LabsError.message("Could not save queues before export.") }
        let transfer = QueueTransfer(ownerXuid:account.xuid,games:queueBook.sorted.map(TransferGame.init))
        try transfer.validate(owner:account.xuid)
        return try JSONEncoder().encode(transfer)
    }
    func importQueueTransfer(_ data: Data) async {
        guard let account, !running, !busy, data.count <= 8*1024*1024 else { error = "Pause Auto Unlock and choose a queue file smaller than 8 MB."; return }
        busy = true; defer { busy = false }
        do {
            let transfer = try JSONDecoder().decode(QueueTransfer.self,from:data); try transfer.validate(owner:account.xuid)
            var imported: [QueueItem] = []
            for (index,saved) in transfer.games.enumerated() {
                guard !queue.contains(where:{ $0.game.id == saved.titleId }) else { throw LabsError.message("This transfer includes a game in the active queue. Load a different saved game before importing; current progress was retained.") }
                guard foreground else { throw LabsError.message("Keep the app open while importing. Existing queues retained.") }
                status = "Checking imported game \(index+1)/\(transfer.games.count)…"
                let game = try await readWithAccount { try await api.game(saved.titleId,account:$0) }
                let fresh = try await readWithAccount { try await api.achievements(game,account:$0) }
                for entry in saved.items {
                    guard var row = fresh.first(where:{ $0.id == entry.achievementId && Catalogue.normalized($0.name) == Catalogue.normalized(entry.name) }) else { throw LabsError.message("Imported achievement does not match this edition: " + entry.name) }
                    row.eventBased = fresh.contains(where: \.eventBased)
                    var item = QueueItem(game:game,achievement:row,delay:entry.delaySeconds)
                    item.remainingDelay = entry.remainingDelaySeconds; item.status = QueueTransfer.importedStatus(entry.status,unlocked:row.unlocked); imported.append(item)
                }
            }
            guard self.account?.xuid == account.xuid else { throw LabsError.message("Account changed during import.") }
            queueBook.saveSeparate(imported); guard writeQueueBook() else { return }
            status = "Imported \(transfer.games.count) saved game queues. Load a game to review; nothing was started."
        } catch { self.error = error.localizedDescription }
    }
    func enqueue(_ selected: [Achievement], game: Game) {
        guard !running else { return }; queueTitle = game.id; titleDetails[game.id] = game
        let items = selected.filter { unavailable($0,game:game) == nil }.map { QueueItem(game:game,achievement:$0,delay:max(0,min(queueDelay,86400))) }
        offerQueue(items, replaceTitle:false); selectedTab = 1
    }
    private func offerQueue(_ items: [QueueItem], replaceTitle: Bool) {
        guard !items.isEmpty else { return }
        if queue.contains(where:{ $0.game.id != items[0].game.id }) && !queue.contains(where:{ $0.game.id == items[0].game.id }) {
            pendingQueueItems = items; pendingQueueReplacesTitle = replaceTitle
        } else { applyQueueItems(items,replaceTitle:replaceTitle) }
    }
    func resolveQueueAddition(separate: Bool) {
        guard !running, !pendingQueueItems.isEmpty else { return }
        let items = pendingQueueItems; pendingQueueItems = []
        if separate { queueBook.saveSeparate(items); _ = writeQueueBook(); status = "Saved a separate game queue. Load it from Saved game queues when ready." }
        else { applyQueueItems(items,replaceTitle:pendingQueueReplacesTitle) }
    }
    private func applyQueueItems(_ items: [QueueItem], replaceTitle: Bool) {
        if replaceTitle, let title = items.first?.game.id { queue.removeAll { $0.game.id == title } }
        for item in items where !queue.contains(where:{ $0.id == item.id }) { queue.append(item) }
        _ = saveQueue(); status = "Queue saved. Review before starting."
    }
    func loadGameQueue(_ id: String, append: Bool) {
        guard !running, !busy, let saved = queueBook.games[id] else { return }
        guard saveQueue() else { return }
        if !append { queueBook.current = []; queue = [] }
        var items = saved.items
        for i in items.indices where items[i].status == "Submitting" { items[i].status = "Interrupted — refresh before retrying" }
        applyQueueItems(items,replaceTitle:false); queueTitle = id
        status = append ? "Saved game added to end of queue. Press Start when ready." : "Saved game loaded. Press Start when ready."
    }
    func removeQueueItem(_ id: String) {
        guard !running, !busy else { return }
        queue.removeAll { $0.id == id }; _ = saveQueue()
    }
    func deleteSavedGameQueue(_ id: String) {
        guard !running, !busy, account != nil, queueBook.games[id] != nil else { return }
        guard saveQueue() else { return }
        let previous = queueBook
        queueBook.deleteGame(id)
        guard writeQueueBook() else { queueBook = previous; return }
        queue = queueBook.current
        status = "Saved game queue deleted, including its loaded items. Other games were retained."
    }
    func resetQueueItem(_ id: String) {
        guard !running, !busy, let i = queue.firstIndex(where:{ $0.id == id }) else { return }
        queue[i].status = "Waiting"; queue[i].remainingDelay = nil; _ = saveQueue()
    }
    func editQueueItem(_ id: String, seconds: Double) {
        guard !running, let i = queue.firstIndex(where:{ $0.id == id }), seconds.isFinite, (0...31_536_000).contains(seconds) else { return }
        queue[i].delay = seconds; queue[i].remainingDelay = nil; _ = saveQueue()
    }
    @discardableResult func saveQueue() -> Bool {
        guard account != nil else { return false }
        queueBook.capture(queue); return writeQueueBook()
    }
    @discardableResult private func writeQueueBook() -> Bool {
        guard let url = queueBookURL else { return false }
        do {
            try queueBook.validate()
            try JSONEncoder().encode(queueBook).write(to:url,options:[.atomic,(continueQueueInBackground || continueSpoofInBackground) ? .completeFileProtectionUntilFirstUserAuthentication : .completeFileProtection])
            return true
        } catch { self.error = "Could not save queue changes."; queueTask?.cancel(); return false }
    }
    private func loadQueue() {
        queueBook = SavedQueueBook(); pendingQueueItems = []
        do {
            if let url = queueBookURL, FileManager.default.fileExists(atPath:url.path) {
                queueBook = try JSONDecoder().decode(SavedQueueBook.self,from:Data(contentsOf:url))
            } else if let url = queueURL, FileManager.default.fileExists(atPath:url.path) {
                // Preserve the original file while migrating the former one-queue format.
                queueBook.capture(try JSONDecoder().decode([QueueItem].self,from:Data(contentsOf:url)))
            }
            try queueBook.validate(); queue = queueBook.current
            for i in queue.indices where queue[i].status == "Submitting" { queue[i].status = "Interrupted — refresh before retrying" }
            queueTitle = queue.first?.game.id ?? ""; _ = saveQueue()
        } catch { self.error = "The saved queue could not be read. Existing files were retained." }
    }
    func startQueue() {
        guard let account, foreground, !running, !busy else { return }
        if case .review(let i) = QueuePolicy.next(queue,stopOnFailure:stopOnFailure) { status = "Review " + queue[i].achievement.name + " before continuing. Use Retry or Skip."; error = status; return }
        guard queue.contains(where:{ $0.status == "Waiting" }) else { status = "No waiting achievements. Review failed or unconfirmed items before retrying."; return }
        running = true; let version = generation
        queueTask = Task { [weak self] in
            guard let self else { return }
            defer {
                let success = !Task.isCancelled && !self.queue.contains { !QueuePolicy.terminal($0.status) }
                if !success { self.backgroundHadFailure = true }
                self.running = false; self.nextAction = nil; self.nextQueueItemID = nil; self.queueTask = nil; self.updateSessionCard()
                UIApplication.shared.isIdleTimerDisabled = self.presenceStarted != nil
                self.finishSessionIfIdle()
                if !self.foreground && !self.presenceBackgroundAllowed { self.presenceTask?.cancel(); Task { await self.stopPresence(reason:"Queue ended without separate background spoofing permission.") } }
            }
            UIApplication.shared.isIdleTimerDisabled = true
            if self.continueQueueInBackground && !self.extendedBackgroundAllowed {
                do {
                    if self.queue.contains(where:{ $0.status == "Waiting" && $0.achievement.eventBased }) { try await self.api.checkCollector(.windows) }
                    try Task.checkCancellation()
                    guard self.foreground else { return }
                    self.requestBackgroundSession(units:self.queue.filter { $0.status == "Waiting" }.count)
                } catch {
                    if Task.isCancelled || error is CancellationError { return }
                    self.rememberTransport(error)
                    if let first = self.queue.firstIndex(where:{ $0.status == "Waiting" }) { self.queue[first].status = "Failed: \(error.localizedDescription)"; self.saveQueue() }
                    self.status = "Connection check failed before an unlock request was created."
                    if self.notifyUnlockFailure { await self.notify("Auto Unlock stopped",body:self.status) }; return
                }
            }
            while case .send(let index) = QueuePolicy.next(self.queue,stopOnFailure:self.stopOnFailure) {
                if Task.isCancelled || (!self.foreground && !self.queueBackgroundAllowed) || version != self.generation { break }
                let item = self.queue[index]; self.nextQueueItemID = item.id; var submitted = false
                let wait = max(0,min(item.remainingDelay ?? item.delay,31_536_000))
                self.nextAction = Date().addingTimeInterval(wait); self.startSessionStatusUpdates(); self.updateSessionCard()
                do {
                    // Re-read current account progress and event metadata before each action.
                    let current = try await self.readWithAccount { try await self.api.achievements(item.game,account:$0) }
                    guard var row = current.first(where: { $0.id == item.achievement.id }), row.name == item.achievement.name else { throw LabsError.message("Achievement metadata changed; rebuild this queue item.") }
                    row.eventBased = current.contains(where: \.eventBased)
                    if row.unlocked { self.queue[index].status = "Already unlocked"; self.saveQueue(); self.continuedQueue.advance(); continue }
                    while let next = self.nextAction, next > Date() {
                        self.queue[index].remainingDelay = max(0,next.timeIntervalSinceNow)
                        guard self.saveQueue() else { throw LabsError.message("Could not checkpoint queue delay.") }
                        try await Task.sleep(nanoseconds:UInt64(min(10,max(0,next.timeIntervalSinceNow))*1_000_000_000))
                    }
                    self.queue[index].remainingDelay = nil
                    try Task.checkCancellation()
                    self.queue[index].status = "Submitting"; self.nextAction = nil; self.updateSessionCard()
                    guard self.saveQueue() else { throw LabsError.message("Queue could not be saved; action was not sent.") }
                    let active = try await self.authorizedAccount(); try Task.checkCancellation()
                    let fresh = try await self.api.achievements(item.game,account:active)
                    if fresh.contains(where:{ $0.id == row.id && $0.unlocked }) { self.queue[index].status = "Already unlocked"; self.saveQueue(); continue }
                    submitted = true; try await self.send(row, game: item.game, account: active, allowBackground:true)
                    self.queue[index].status = "Accepted — verify progress"; self.saveQueue(); self.continuedQueue.advance()
                } catch is CancellationError {
                    if self.queue[index].status == "Submitting" { self.queue[index].status = "Interrupted — refresh before retrying"; if self.notifyUnlockFailure { await self.notify("Auto Unlock needs attention",body:item.achievement.name + ": request interrupted; refresh before retrying.") } }; self.saveQueue(); break
                } catch {
                    if Task.isCancelled {
                        if self.queue[index].status == "Submitting" { self.queue[index].status = "Interrupted — refresh before retrying"; if self.notifyUnlockFailure { await self.notify("Auto Unlock needs attention",body:item.achievement.name + ": request interrupted; refresh before retrying.") } }
                        self.saveQueue(); break
                    }
                    await self.failQueue(index,error:error,mayHaveSent:submitted)
                    if self.stopOnFailure && QueuePolicy.needsReview(self.queue[index].status) { break }
                }
            }
            if !Task.isCancelled && !self.queue.contains(where: { !QueuePolicy.terminal($0.status) }) {
                self.status = "Queue finished. Accepted requests still need progress verification."
                if self.notifyCompletion { await self.notify("Auto unlock finished", body: "Refresh achievements to verify accepted requests.") }
            }
        }
    }
    func pauseQueue() { if running { backgroundHadFailure = true; if let next = nextAction, let i = queue.firstIndex(where:{ $0.status == "Waiting" }) { queue[i].remainingDelay = max(0,next.timeIntervalSinceNow); _ = saveQueue() } }; queueTask?.cancel(); nextAction = nil; finishSessionIfIdle(); status = "Queue paused. Saved items can be reviewed before resuming." }
    func removeCompleted() async {
        guard let account, !running, !busy else { return }; busy = true; defer { busy = false }
        do {
            var completed = Set<String>()
            for game in Dictionary(grouping: queue, by: { $0.game.id }).values.compactMap({ $0.first?.game }) {
                let loaded = try await readWithAccount { try await api.achievements(game,account:$0) }
                for row in loaded where row.unlocked { completed.insert(game.id + "/" + row.id) }
            }
            queue.removeAll { completed.contains($0.id) }; saveQueue(); status = "Removed achievements confirmed unlocked by Xbox."
        } catch { self.error = error.localizedDescription }
    }
    func startPresence(_ title: String) {
        guard let account, presenceTask == nil, !presenceStopping, foreground, JSON.numeric(title) else { return }
        spoofStopReason = ""; spoofCleanupStatus = ""; lastHeartbeat = nil; presenceTitle = title; presenceRunning = true; spoofDiagnostics = ""; spoofStatus = "Sending heartbeat…"
        presenceTask = Task { [weak self] in
            guard let self else { return }
            defer { self.presenceStarted = nil; self.presenceRunning = false; self.presenceTask = nil; self.updateSessionCard(); self.presenceDeadline = nil; if !self.presenceStopping { self.extendedAudio.stop() }; self.finishSessionIfIdle(); if !self.foreground && self.running && !self.queueBackgroundAllowed { self.pauseQueue() }; UIApplication.shared.isIdleTimerDisabled = self.running }
            do {
                let active = try await self.authorizedAccount(); try Task.checkCancellation(); try await self.api.presence(title, account: active); self.presenceStarted = Date(); self.presenceCleanupNeeded = true
                if self.continueSpoofInBackground {
                    let minutes = max(5,min(120,self.spoofBackgroundMinutes)); self.presenceDeadline = Date().addingTimeInterval(minutes*60)
                    if self.useExtendedAudio {
                        try self.extendedAudio.start { [weak self] reason in
                            guard let self else { return }; self.spoofStopReason = reason
                            Task { await self.stopPresence(reason:reason, notifyStop:true); if self.stopOnPresenceFailure { self.pauseQueue() } }
                        }
                        if self.continuedQueue.hasRequest { self.continuedQueue.finish(success:true) }; self.startSessionStatusUpdates(); self.updateSessionCard()
                    } else {
                        self.requestBackgroundSession(units:max(1,Int(ceil(minutes/4))))
                        self.continuedQueue.advance()
                    }
                }
                self.lastHeartbeat = Date(); self.startSessionStatusUpdates(); self.updateSessionCard(); self.spoofStatus = "Heartbeat accepted. Xbox-recorded playtime can update later."; UIApplication.shared.isIdleTimerDisabled = true
                while !Task.isCancelled { try await Task.sleep(nanoseconds: 240_000_000_000); try Task.checkCancellation(); guard self.foreground || self.presenceBackgroundAllowed else { throw CancellationError() }; let active = try await self.authorizedAccount(); try Task.checkCancellation(); try await self.renewHeartbeat(title,account:active); self.lastHeartbeat = Date(); self.spoofStatus = "Heartbeat accepted at " + Date().formatted(date:.omitted,time:.standard); if self.continueSpoofInBackground { self.continuedQueue.advance() }; self.updateSessionCard() }
            } catch is CancellationError { }
            catch {
                if Task.isCancelled { return }
                self.rememberTransport(error); self.backgroundHadFailure = true; self.spoofStopReason = "Heartbeat failed: " + error.localizedDescription; self.spoofStatus = self.spoofStopReason
                if let failure = error as? XboxHTTPFailure {
                    self.spoofDiagnostics = "Title spoofing · " + Date().formatted(date:.numeric,time:.standard) + "\nTitle ID: " + title + "\nSign-in profile: " + (self.account?.loginProfile ?? "Manual / previous beta") + "\nPresence request: " + "Account authorization" + "\n" + failure.diagnostic
                    if failure.status == 403 {
                        do {
                            let current = self.account ?? account
                            let profile = try await self.api.connect(current.authorization)
                            self.spoofDiagnostics += profile.xuid == current.xuid ? "\nIndependent profile check: accepted for the same account.\nThe refusal is specific to heartbeat access; no heartbeat retry was sent." : "\nIndependent profile check: identity mismatch."
                        } catch let probe as XboxHTTPFailure { self.spoofDiagnostics += "\nIndependent profile check: HTTP \(probe.status)." }
                        catch { self.spoofDiagnostics += "\nIndependent profile check: unavailable." }
                    }
                }
                var guidance = ""
                if let failure = error as? XboxHTTPFailure, failure.status == 403, self.account?.loginProfile != "PC transfer" {
                    guidance = "\nFor title spoofing, scan the PC app’s full sign-in QR. An event-token-only QR does not supply spoofing authorization."
                }
                self.error = "Title spoofing stopped: \(error.localizedDescription)" + guidance
                if self.stopOnPresenceFailure { self.pauseQueue() }
                if self.notifyPresenceFailure { await self.notify("Title spoofing stopped", body: "Open Achievement Labs to review the heartbeat error.") }
            }
        }
    }
    private func renewHeartbeat(_ title: String, account: Account) async throws {
        for attempt in 0..<3 {
            do { try Task.checkCancellation(); try await api.presence(title,account:account); return }
            catch {
                if Task.isCancelled || !uncertain(error) || attempt == 2 { throw error }
                spoofStatus = "Heartbeat connection interrupted; reconnecting…"; updateSessionCard()
                try await Task.sleep(nanoseconds:UInt64(attempt+1)*2_000_000_000)
            }
        }
    }
    func terminating() { queueTask?.cancel(); presenceTask?.cancel(); liveStatus.end(); extendedAudio.stop() }
    func stopPresence(reason: String = "Stopped by user.", notifyStop: Bool = false) async {
        guard !presenceStopping, presenceTask != nil || presenceStarted != nil || presenceCleanupNeeded else { return }
        presenceStopping = true; presenceCleanupNeeded = false
        defer { presenceStopping = false; extendedAudio.stop(); finishSessionIfIdle(); if !foreground && running && !queueBackgroundAllowed { pauseQueue() } }
        if spoofStopReason.isEmpty { spoofStopReason = reason }
        presenceTask?.cancel()
        if notifyStop && notifyPresenceFailure { await notify("Title spoofing stopped", body:spoofStopReason) }
        if let presenceTask { await presenceTask.value }
        spoofStatus = spoofStopReason
        if let account {
            do { try await api.presence(nil, account: account); spoofCleanupStatus = "Xbox stop request accepted." }
            catch { spoofCleanupStatus = "Xbox stop request failed (\((error as NSError).domain) \((error as NSError).code)). The last heartbeat may take time to expire." }
        }
        extendedAudio.stop()
        spoofDiagnostics = "Stopped: " + spoofStopReason + "\nBackground status: " + continuedQueue.status + "\nAudio mode: " + (useExtendedAudio ? "Extended" : "System task") + " · " + extendedAudio.status + "\n" + spoofCleanupStatus

    }
    func updateBackgroundPreference() {
        guard !running, !presenceRunning else { return }
        do { if let account { try saveAccount(account) }; _ = saveQueue() }
        catch { continueQueueInBackground = false; continueSpoofInBackground = false; self.error = "Could not update protected storage for background access." }
    }
    func requestBackgroundSession(units: Int) {
        guard foreground else { return }
        if !continuedQueue.hasRequest { backgroundHadFailure = false }
        continuedQueue.start(total:units) { [weak self] in
            guard let self else { return }
            if self.extendedBackgroundAllowed { self.updateSessionCard(); return }
            self.pauseQueue(); self.presenceTask?.cancel()
            Task { await self.stopPresence(reason:"iOS ended background permission.", notifyStop:true) }
            self.status = "iOS ended background time. Review before resuming."
            self.spoofStopReason = "iOS ended background permission."
        }
        updateSessionCard(); startSessionStatusUpdates()
    }
    func startSessionStatusUpdates() {
        if sessionStatusTask == nil {
            let token = UUID(); sessionStatusGeneration = token
            sessionStatusTask = Task { [weak self] in
                guard let self else { return }; defer { if self.sessionStatusGeneration == token { self.sessionStatusTask = nil } }
                while (self.running || self.presenceRunning) && !Task.isCancelled {
                    do { try await Task.sleep(nanoseconds:15_000_000_000); try Task.checkCancellation() } catch { return }
                    if let end = self.presenceDeadline, end <= Date(), (self.extendedAudio.active || !(self.continueSpoofWithQueue && self.queueBackgroundAllowed)) { Task { await self.stopPresence(reason:"Configured spoof session limit reached.", notifyStop:true) }; return }
                    if self.extendedAudio.active && !self.extendedAudio.playing { Task { await self.stopPresence(reason:"Extended audio playback stopped. Resume manually.", notifyStop:true) }; return }
                    // Live Activity updates are sent on transitions and heartbeats.
                }
            }
        }
    }
    func updateSessionCard() {
        let accepted = queue.filter { $0.status.hasPrefix("Accepted") || $0.status == "Already unlocked" }.count
        let queueText = running ? "Queue \(accepted)/\(queue.count) processed" : (queue.isEmpty ? "No queue running" : "Queue ready")
        let spoofText: String
        if let start = presenceStarted, presenceRunning {
            let seconds = max(0,Int(Date().timeIntervalSince(start)))
            spoofText = String(format:"Spoof %02d:%02d",seconds/60,seconds%60)
        } else { spoofText = "Spoof stopped" }
        continuedQueue.update(title:presenceRunning && !running ? "Title spoofing active" : "Achievement Labs",subtitle:spoofText+" · "+queueText)
        let current = nextQueueItem
        let expiry = SessionTiming.spoofExpiry(lastHeartbeat:lastHeartbeat,started:presenceStarted)
        let stale = SessionTiming.staleDate(now:Date(),spoofExpiry:presenceRunning ? expiry : nil,nextAction:nextAction,queueRunning:running)
        liveStatus.update(.init(spoofStarted:presenceStarted,queueHandled:queue.filter { QueuePolicy.terminal($0.status) }.count,queueTotal:queue.count,queueRunning:running,nextAction:nextAction,spoofGame:gameForTitle(presenceTitle)?.name ?? presenceTitle,queueGame:current?.game.name ?? "",nextAchievement:current?.achievement.name ?? "",updatedAt:Date(),validUntil:stale,spoofExpires:expiry,queueItemID:current?.id),enabled:presenceRunning || running)
    }
    func finishSessionIfIdle() {
        let queueWanted = running && queueTask?.isCancelled != true && continueQueueInBackground
        let spoofWanted = presenceRunning && presenceTask?.isCancelled != true && ((continueSpoofInBackground && (presenceDeadline ?? .distantPast) > Date()) || (continueSpoofWithQueue && queueWanted))
        if queueWanted || spoofWanted || (foreground && (running || presenceRunning)) { updateSessionCard(); return }
        continuedQueue.finish(success:!backgroundHadFailure); liveStatus.end()
        sessionStatusGeneration = UUID(); sessionStatusTask?.cancel(); sessionStatusTask = nil
    }
    func rememberTransport(_ error: Error) {
        if let failure = error as? XboxTransportFailure { transportDiagnostics = failure.localizedDescription }
        else if let failure = error as? XboxHTTPFailure { transportDiagnostics = failure.diagnostic }
    }
    func checkSecureConnections() async {
        guard !checkingConnections, !running, !presenceRunning else { return }
        checkingConnections = true; defer { checkingConnections = false }
        let urls = [CollectorRoute.mobile.url,CollectorRoute.windows.url,URL(string:"https://achievements.xboxlive.com/")!,URL(string:"https://presence-heartbeat.xboxlive.com/")!]
        var lines = ["Unauthenticated TLS checks · " + Date().formatted()]
        for url in urls { lines.append(await api.probe(url)) }
        transportDiagnostics = lines.joined(separator:"\n")
    }
    func background() {
        foreground = false
        if running && !queueBackgroundAllowed { pauseQueue() }
        if !presenceBackgroundAllowed { spoofStopReason = continueSpoofInBackground ? "iOS did not grant or no longer permits background spoofing." : "Background spoofing is off. Enable it in Settings → App."; presenceTask?.cancel(); Task { await stopPresence(reason:spoofStopReason, notifyStop:true) } }
        if queueBackgroundAllowed || presenceBackgroundAllowed { status = "Session continuing while iOS permits background time." }
        else { finishSessionIfIdle(); status = "Paused in background. Review and resume when you return." }
    }
    #if DEBUG
    func audioSelfTest() async {
        guard account == nil else { return }
        var result: [String:Any] = [:]
        do {
            try extendedAudio.start(interrupted:{ _ in })
            try await Task.sleep(nanoseconds:1_000_000_000)
            result["playing"] = extendedAudio.playing && extendedAudio.active
            extendedAudio.stop(); result["stopped"] = !extendedAudio.active && !extendedAudio.playing
        } catch { result["playing"] = false; result["error"] = (error as NSError).domain + " " + String((error as NSError).code) }
        result["audioManifest"] = (Bundle.main.object(forInfoDictionaryKey:"UIBackgroundModes") as? [String])?.contains("audio") == true
        result["liveManifest"] = Bundle.main.object(forInfoDictionaryKey:"NSSupportsLiveActivities") as? Bool == true
        if let url = FileManager.default.urls(for:.documentDirectory,in:.userDomainMask).first?.appendingPathComponent("Audio-self-test.json") { try? JSONSerialization.data(withJSONObject:result).write(to:url,options:.atomic) }
    }
    func collectorSelfTest() async {
        guard account == nil else { return }
        let result = await XboxService().probe(CollectorRoute.windows.url)
        let ats = (Bundle.main.object(forInfoDictionaryKey:"NSAppTransportSecurity") as? [String:Any]) ?? [:]
        let exceptions = ats["NSExceptionDomains"] as? [String:Any] ?? [:]
        let scoped = exceptions.keys.sorted() == ["v20.events.data.microsoft.com"] && ats["NSAllowsArbitraryLoads"] == nil
        let document = ["result":result,"passed":result.contains("TLS accepted"),"scopedATS":scoped] as [String:Any]
        if let url = FileManager.default.urls(for:.documentDirectory,in:.userDomainMask).first?.appendingPathComponent("Collector-self-test.json") { try? JSONSerialization.data(withJSONObject:document).write(to:url,options:.atomic) }
    }
    func backgroundSelfTest() async {
        // Only reachable in offline preview, which blocks every Xbox request.
        guard ProcessInfo.processInfo.arguments.contains("--ui-preview=queue") else { return }
        do { try await Task.sleep(nanoseconds:1_000_000_000) } catch { return }
        continuedQueue.start(total:2,expired:{})
        do { try await Task.sleep(nanoseconds:9_000_000_000) } catch { return }
        let granted = continuedQueue.active
        if granted {
            // Two completed synthetic local jobs, never a heartbeat or unlock.
            _ = Data("offline-one".utf8).base64EncodedString(); continuedQueue.advance()
            _ = Data("offline-two".utf8).base64EncodedString(); continuedQueue.advance()
        }
        continuedQueue.finish(success:granted)
        let identifiers = Bundle.main.object(forInfoDictionaryKey:"BGTaskSchedulerPermittedIdentifiers") as? [String] ?? []
        let modes = Bundle.main.object(forInfoDictionaryKey:"UIBackgroundModes") as? [String] ?? []
        let result: [String:Any] = ["available":continuedQueue.available,"granted":granted,"finishedInactive":!continuedQueue.active,"manifestValid":identifiers.contains("org.achievementlabs.mobile.queue.*") && modes.contains("processing")]
        if let url = FileManager.default.urls(for:.documentDirectory,in:.userDomainMask).first?.appendingPathComponent("Background-self-test.json") {
            try? JSONSerialization.data(withJSONObject:result).write(to:url,options:.atomic)
        }
    }
    #endif
    func active() { foreground = true; updateSessionCard(); Task { await maintainOnOpen() } }
    func requestNotifications() async { do { _ = try await UNUserNotificationCenter.current().requestAuthorization(options: [.alert,.sound]) } catch { self.error = error.localizedDescription } }
    private func notify(_ title: String, body: String) async {
        let content = UNMutableNotificationContent(); content.title = title; content.body = body; content.sound = .default
        try? await UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }
    func gameForTitle(_ id: String) -> Game? {
        if var game = games.first(where: { $0.id == id }) {
            if game.image.isEmpty { game.image = titleDetails[id]?.image ?? "" }
            return game
        }
        return titleDetails[id]
    }
    func hasMythic(_ game: Game) -> Bool { catalogue?.isHub(game) != true && (baseCompleted.contains(game.id) || game.complete) }
    var mythicCount: Int { games.filter(hasMythic).count }
    var libraryAchievements: Int { games.reduce(0) { $0 + $1.unlocked } }
    func lookupTitle(_ id: String) async {
        guard let account, JSON.numeric(id) else { return }; let version = generation
        if let game = gameForTitle(id), !game.image.isEmpty { titleDetails[id] = game; return }
        do { let game = try await readWithAccount { try await api.game(id,account:$0) }; guard !Task.isCancelled, version == generation else { return }; titleDetails[id] = withTotal(game) }
        catch { /* Keep typed IDs usable when title lookup is unavailable. */ }
    }
    func resolveFoundGame(_ candidate: Game) async throws -> Game {
        guard account != nil else { throw LabsError.message("Sign in to Xbox to open achievements.") }
        let version = generation
        var game = gameForTitle(candidate.id) ?? candidate
        do { game = try await readWithAccount { try await api.game(candidate.id,account:$0) } }
        catch { try Task.checkCancellation() /* Public metadata remains usable when TitleHub has no history row. */ }
        guard version == generation else { throw LabsError.message("Account changed. Open the game again.") }
        if game.name.isEmpty { game.name = candidate.name }
        if game.platforms.isEmpty { game.platforms = candidate.platforms }
        if candidate.legacy { game.platforms = candidate.platforms }
        game = withTotal(game); titleDetails[game.id] = game
        return game
    }
    func readPlaytime(_ title: String) async {
        guard let account, JSON.numeric(title) else { return }; let version = generation
        do { if let minutes = try await readWithAccount({ try await api.playedMinutes(title,account:$0) }), version == generation, !Task.isCancelled { playedHours[title] = minutes / 60 } }
        catch { /* Missing playtime is displayed as unavailable, never zero. */ }
    }
    func buildTimingQueue() async {
        guard let account, !busy, !running, JSON.numeric(queueTitle) else { return }
        busy = true; defer { busy = false }; let version = generation; let title = queueTitle
        status = "Reading reference profile and achievement times…"
        do {
            let game: Game
            if let known = gameForTitle(title) { game = known } else { game = try await readWithAccount { try await api.game(title,account:$0) } }
            let reference = try await readWithAccount { try await api.profile(gamertag:referenceGamertag,account:$0) }
            let dated = try await readWithAccount { try await api.achievements(game,account:$0,xuid:reference.xuid) }
            var own = try await readWithAccount { try await api.achievements(game,account:$0) }
            if own.contains(where: \.eventBased) { for i in own.indices { own[i].eventBased = true } }
            let plan = try TimingPlan.build(game:game,own:own,reference:dated,gamertag:reference.gamertag) { unavailable($0,game:game) == nil }
            guard version == generation else { return }
            guard !plan.items.isEmpty else { throw LabsError.message("No matching locked achievements with available actions were found. Existing queue retained.") }
            offerQueue(plan.items,replaceTitle:true); titleDetails[title] = game
            referenceInfo = "\(reference.gamertag) · \(plan.items.count) queued · \(plan.skipped) skipped"
            rememberCompletion(own,game:game); saveQueue(); status = "Timing queue loaded. Review delays, then start when ready."
        } catch { self.error = error.localizedDescription; status = "Timing import failed; review the existing queue." }
    }
    func maintainOnOpen(forceLibrary: Bool = false) async {
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains(where: { $0.hasPrefix("--ui-preview=") || $0 == "--storage-self-test" || $0 == "--web-callback-self-test" }) { return }
        #endif
        guard foreground, !running, !presenceRunning else { return }
        if lifecycleRunning {
            if forceLibrary, autoRefreshLibrary, account != nil, !busy { await refreshGames() }
            return
        }
        lifecycleRunning = true; defer { lifecycleRunning = false }
        if autoRefreshLibrary, account != nil, !busy, forceLibrary || Date().timeIntervalSince(lastLibraryRefresh) > 60 { await refreshGames() }
        if autoSyncEvents, Date().timeIntervalSince(lastEventsCheck) > 3600 { await syncEvents() }
        if checkMobileUpdates, Date().timeIntervalSince(lastUpdateCheck) > 86400 { await checkForMobileUpdates() }
    }
    func syncEvents() async {
        guard !eventsSyncing, !running else { return }; eventsSyncing = true; defer { eventsSyncing = false }
        eventSyncStatus = "Downloading the validated upstream event feed…"
        do {
            let feed = try await PublicSync().events()
            guard let catalogue else { throw LabsError.message("Bundled catalogue is unavailable.") }
            let added = try catalogue.merge(feed,cache:directory.appendingPathComponent("synced-events.json"))
            lastEventsCheck = Date(); eventSyncStatus = "\(feed.upstreamRelease) · \(added) new titles added · checked " + Date().formatted(date:.abbreviated,time:.shortened)
        } catch { eventSyncStatus = error.localizedDescription + " Existing mappings retained." }
    }
    func importEventPack(_ data: Data) {
        guard !eventsSyncing, !running else { eventSyncStatus = "Pause Auto Unlock before importing event data."; return }
        eventsSyncing = true; defer { eventsSyncing = false }
        do {
            guard data.count <= 32*1024*1024, let catalogue else { throw LabsError.message("Event pack exceeds 32 MB or catalogue is unavailable.") }
            let feed = try JSONDecoder().decode(EventFeed.self,from:data)
            let added = try catalogue.merge(feed,cache:directory.appendingPathComponent("synced-events.json"))
            eventSyncStatus = "Imported \(added) new titles from event pack (Testing). Existing mappings retained."
        } catch { eventSyncStatus = "Could not import event pack: " + error.localizedDescription + " Existing data retained. Choose a pack containing templates and mappings." }
    }
    func checkForMobileUpdates() async {
        guard !updateChecking else { return }; updateChecking = true; defer { updateChecking = false }
        updateStatus = "Checking published iOS releases…"
        do {
            let latest = try await PublicSync().release(); lastUpdateCheck = Date()
            let current = Bundle.main.object(forInfoDictionaryKey:"CFBundleShortVersionString") as? String ?? "0.6.0"
            if let latest, MobileRelease.newer(latest.version,than:current) {
                availableUpdate = latest; updateStatus = "iOS \(latest.version) is available. Sign and install its IPA through Signulous."
                if notifyMobileUpdates, UserDefaults.standard.string(forKey:"lastNotifiedMobileVersion") != latest.version {
                    await notify("Achievement Labs update",body:"iOS \(latest.version) is available. Open Settings to download it.")
                    UserDefaults.standard.set(latest.version,forKey:"lastNotifiedMobileVersion")
                }
            } else { availableUpdate = nil; updateStatus = latest == nil ? "No published iOS release found yet. Windows releases are ignored." : "iOS \(current) is up to date." }
        } catch { updateStatus = error.localizedDescription }
    }
    func preparePCTransfer(_ data: Data, key: String) async {
        guard !busy, !running, !presenceRunning else { return }; busy = true; defer { busy = false }
        pendingPCAccount = nil; pendingPCEventOnly = false; pendingPCSignInOnly = false; pcSyncStatus = "Verifying the transferred Xbox account…"
        do {
            let transfer = try PCTransfer.open(data,key:key)
            let authorization: String
            if transfer.eventOnly {
                guard account?.xuid == transfer.xuid else { throw LabsError.message("Sign in to the same Xbox account on iOS before importing this event-token-only QR.") }
                authorization = try await authorizedAccount().authorization
            } else { authorization = transfer.authorization }
            var verified = try await api.connect(authorization)
            guard verified.xuid == transfer.xuid else { throw LabsError.message("The transferred authorization belongs to a different account.") }
            verified.eventToken = transfer.signInOnly ? (account?.xuid == transfer.xuid ? account!.eventToken : "") : (transfer.eventToken.isEmpty ? "" : try Tokens.normalize(transfer.eventToken,event:true))
            verified.authorizationExpiresAt = transfer.authorizationExpiresAt.flatMap(ServiceDates.parse)
            pendingPCSignInOnly = transfer.signInOnly
            pendingPCEventOnly = transfer.eventOnly
            verified.loginProfile = "PC transfer"; pendingPCAccount = verified; pendingPCExpiry = Date(timeIntervalSince1970:Double(transfer.expiresAt))
            pcSyncStatus = transfer.signInOnly ? "Verified \(verified.gamertag). Connect this PC sign-in; any existing event token for this account will be retained." : transfer.eventOnly ? "Verified \(verified.gamertag). Import the event token to your existing mobile sign-in." : "Verified \(verified.gamertag). Choose whether to connect this account or import only its event token."
        } catch { pcSyncStatus = error.localizedDescription }
    }
    func usePCTransfer(eventOnly: Bool) {
        guard let imported = pendingPCAccount, !busy, !running, !presenceRunning else { return }
        do {
            guard let pendingPCExpiry, pendingPCExpiry > Date() else { pendingPCAccount = nil; throw LabsError.message("The transfer expired. Export a fresh file from PC.") }
            guard !pendingPCEventOnly || eventOnly else { throw LabsError.message("This QR contains only an event token. Choose Import event token only.") }
            if eventOnly {
                guard !pendingPCSignInOnly else { throw LabsError.message("This transfer contains sign-in only.") }
                guard var current = account, current.xuid == imported.xuid else { throw LabsError.message("Event-only sync requires the same Xbox account on both devices.") }
                guard !imported.eventToken.isEmpty else { throw LabsError.message("The PC transfer contains no event token.") }
                current.eventToken = imported.eventToken; try saveAccount(current)
            } else {
                try saveAccount(imported); generation = UUID(); games = []; rows = []; queue = []; selectedGame = nil; titleDetails = [:]; playedHours = [:]; loadQueue(); loadCompletion()
                Task { await maintainOnOpen(forceLibrary:true) }
            }
            pendingPCAccount = nil; pcSyncStatus = eventOnly ? "Event token imported; remembered mobile sign-in retained." : "PC account imported. Renewal credentials remain on PC; sync again when this authorization expires."
        } catch { pcSyncStatus = error.localizedDescription }
    }
    #if DEBUG
    private func configurePreview(_ page: String) {
        account = Account(authorization:"synthetic-offline",xuid:"0",gamertag:"Preview profile")
        account?.gamerscore = "12,345"
        let first = try! Game(["titleId":"7","name":"Example completed game","devices":["XboxOne","XboxSeries"],"achievement":["currentAchievements":50,"totalAchievements":50]])
        let second = try! Game(["titleId":"8","name":"A longer example game title for layout checks","devices":["PC","XboxOne"],"achievement":["currentAchievements":24,"totalAchievements":80]])
        games = [first,second]; baseCompleted = [first.id]; titleDetails = [first.id:first,second.id:second]
        queueTitle = second.id; presenceTitle = second.id; referenceGamertag = "Reference profile"
        let row = try! Achievement(["id":"1","name":"First steps","lockedDescription":"Complete the first mission.","serviceConfigId":"00000000-0000-0000-0000-000000000001","gamerscore":20])
        queue = [QueueItem(game:second,achievement:row,delay:120)]
        referenceInfo = "Reference profile · 1 queued · 2 skipped"; status = "Timing queue loaded. Review delays, then start when ready."
        spoofStatus = "No heartbeat sent."; playedHours[second.id] = 12.5
        selectedTab = (page == "queue" || page == "session-settings") ? 1 : page == "spoof" ? 1 : page == "xbox360" ? 5 : page.hasPrefix("settings") ? 3 : page == "home" ? 4 : 0
    }
    #endif
    private struct CompletionCache: Codable { var mythic: Set<String>; var checked: Set<String>; var totals: [String:Int] }
    private var completionURL: URL? { account.map { directory.appendingPathComponent("completion-\($0.xuid).json") } }
    private func loadCompletion() {
        baseCompleted = []; checkedCompletion = []; savedTotals = [:]
        guard let url = completionURL, let data = try? Data(contentsOf:url), let saved = try? JSONDecoder().decode(CompletionCache.self,from:data) else { return }
        baseCompleted = saved.mythic; checkedCompletion = saved.checked; savedTotals = saved.totals
    }
    private func rememberCompletion(_ loaded: [Achievement], game: Game) {
        guard !loaded.isEmpty, !game.legacy || loaded.filter(\.unlocked).count >= game.unlocked else { return }
        checkedCompletion.insert(game.id); savedTotals[game.id] = loaded.count
        if catalogue?.baseComplete(loaded,game:game) == true { baseCompleted.insert(game.id) }
        guard let url = completionURL else { return }
        do { try JSONEncoder().encode(CompletionCache(mythic:baseCompleted,checked:checkedCompletion,totals:savedTotals)).write(to:url,options:[.atomic,.completeFileProtection]) }
        catch { self.error = "Could not save completion indicators." }
    }
    private func withTotal(_ input: Game) -> Game {
        var game = catalogue?.applyingTotal(input) ?? input
        if game.total <= 0, let total = savedTotals[game.id], total >= game.unlocked { game.total = total }
        return game
    }
    func scanCompletion() async {
        guard let account, !busy, !running else { return }; busy = true; scanningCompletion = true; defer { busy = false; scanningCompletion = false }; let version = generation
        let pending = games.filter { !checkedCompletion.contains($0.id) }
        var failures = 0
        for (n,game) in pending.enumerated() {
            guard scanningCompletion, foreground, version == generation, !Task.isCancelled else { break }
            status = "Completion scan \(n+1)/\(pending.count) · \(game.name)"
            do {
                let loaded = try await readWithAccount { try await api.achievements(game,account:$0) }; rememberCompletion(loaded,game:game)
                if !loaded.isEmpty, (!game.legacy || loaded.filter(\.unlocked).count >= game.unlocked), let i = games.firstIndex(where: { $0.id == game.id }) { games[i].total = loaded.count; games[i].unlocked = loaded.filter(\.unlocked).count }
            } catch { failures += 1; if error.localizedDescription.contains("429") { break } }
            try? await Task.sleep(nanoseconds:300_000_000)
        }
        status = "Completion checked for \(checkedCompletion.count) titles · \(failures) failed this scan. Unchecked titles can be scanned again."
    }

}

#if DEBUG
/// Screenshot fixtures cannot make network requests, even if a UI task runs.
private final class PreviewOfflineProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() { client?.urlProtocol(self,didFailWithError:URLError(.cancelled)) }
    override func stopLoading() {}
}
#endif
