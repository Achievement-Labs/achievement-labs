import Foundation
import Combine
import BackgroundTasks
import LabsMobileCore

/// Best-effort iOS 26 continued processing for a finite, explicitly started queue.
/// No background fetch, silent audio, automatic restart, or synthetic progress.
@MainActor final class ContinuedQueue: ObservableObject {
    @Published private(set) var status = "Background continuation is off."
    @Published private(set) var active = false
    private var lease = BackgroundQueueLease()
    private var platformTask: AnyObject?
    private var requestIdentifier: String?
    private var watchdog: Task<Void,Never>?
    var hasRequest: Bool { lease.identifier != nil }
    var available: Bool { if #available(iOS 26.0, *) { return true }; return false }

    func start(total: Int, expired: @escaping @MainActor () -> Void) {
        guard !hasRequest else { addWork(total); return }
        guard #available(iOS 26.0, *) else { status = "Requires iOS 26. Keep this app open."; return }
        let token = lease.request(total:total)
        let identifier = (Bundle.main.bundleIdentifier ?? "org.achievementlabs.mobile") + ".queue." + token.uuidString
        requestIdentifier = identifier
        let registered = BGTaskScheduler.shared.register(forTaskWithIdentifier:identifier,using:.main) { [weak self] task in
            Task { @MainActor [weak self] in
                guard let self, let continued = task as? BGContinuedProcessingTask else { task.setTaskCompleted(success:false); return }
                guard self.lease.grant(token) else { continued.setTaskCompleted(success:false); return }
                self.watchdog?.cancel(); self.watchdog = nil
                self.platformTask = continued; self.active = true
                continued.progress.totalUnitCount = Int64(self.lease.total)
                continued.progress.completedUnitCount = Int64(self.lease.completed)
                continued.expirationHandler = { [weak self] in
                    Task { @MainActor [weak self] in
                        guard let self, self.lease.identifier == token else { return }
                        self.finish(success:false)
                        self.status = "iOS ended background time. Queue paused; review before resuming."
                        expired()
                    }
                }
                self.status = "iOS granted background session time. It can end this permission at any time."
            }
        }
        guard registered else { finish(success:false); status = "Background handler unavailable. Keep the app open."; return }
        let request = BGContinuedProcessingTaskRequest(identifier:identifier,title:"Achievement Labs session",subtitle:"Queue and spoofing · requests need verification")
        request.strategy = .fail // Never leave an unattended queued request to start later.
        do {
            try BGTaskScheduler.shared.submit(request)
            status = "Requesting background time. Keep the app open until permission is granted."
            watchdog = Task { [weak self] in
                do { try await Task.sleep(nanoseconds:8_000_000_000); try Task.checkCancellation() } catch { return }
                guard let self, self.lease.identifier == token, !self.active else { return }
                self.finish(success:false); self.status = "iOS did not grant background time. Queue can run only while this app is open."
            }
        } catch { finish(success:false); status = "iOS declined background continuation (\((error as NSError).domain) \((error as NSError).code)). Keep the app open." }
    }
    func addWork(_ count: Int) {
        lease.addWork(count)
        if #available(iOS 26.0, *), let task = platformTask as? BGContinuedProcessingTask { task.progress.totalUnitCount = Int64(lease.total) }
    }
    func update(title: String, subtitle: String) {
        if #available(iOS 26.0, *), let task = platformTask as? BGContinuedProcessingTask { task.updateTitle(title,subtitle:subtitle) }
    }
    func advance() {
        lease.advance()
        if #available(iOS 26.0, *), let task = platformTask as? BGContinuedProcessingTask {
            task.progress.completedUnitCount = Int64(lease.completed)
        }
    }
    func finish(success: Bool) {
        watchdog?.cancel(); watchdog = nil
        guard let token = lease.identifier, lease.finish(token) else { return }
        active = false
        if #available(iOS 26.0, *), let task = platformTask as? BGContinuedProcessingTask {
            task.expirationHandler = nil; task.updateTitle(success ? "Background task completed" : "Background task ended",subtitle:"Open app for current queue and spoofing status"); task.setTaskCompleted(success:success)
        }
        platformTask = nil
        if let identifier = requestIdentifier { BGTaskScheduler.shared.cancel(taskRequestWithIdentifier:identifier) }
        requestIdentifier = nil
        status = success ? "Background session finished. Verify Xbox progress." : "System background task ended; check current session status."
    }
}


