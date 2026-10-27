import AppKit
import UserNotifications

private final class NotificationDelegate: NSObject, UNUserNotificationCenterDelegate {
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completion: @escaping (UNNotificationPresentationOptions) -> Void) {
        completion([.banner, .sound, .list])
    }
    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completion: @escaping () -> Void) {
        DispatchQueue.main.async { NSApp.activate(ignoringOtherApps: true) }
        completion()
    }
}
private let notificationDelegate = NotificationDelegate()
public typealias NotificationReply = @convention(c) (Int64, Int32) -> Void

// Runs inside the actual .app process, so macOS permission belongs to Achievement Labs.
// Query mode never asks for permission or delivers a notification (also used in CI).
@_cdecl("LabsMacNotification")
public func labsMacNotification(_ titlePointer: UnsafePointer<CChar>, _ bodyPointer: UnsafePointer<CChar>,
                                _ explicitTest: Int32, _ queryOnly: Int32, _ identifier: Int64,
                                _ reply: @escaping NotificationReply) {
    let title = String(cString: titlePointer), body = String(cString: bodyPointer)
    guard Bundle.main.bundleIdentifier != nil else { reply(identifier, 3); return }
    DispatchQueue.main.async {
        let center = UNUserNotificationCenter.current()
        center.delegate = notificationDelegate
        func submit() {
            let content = UNMutableNotificationContent()
            content.title = title; content.body = body; content.sound = .default
            let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
            center.add(request) { error in reply(identifier, error == nil ? 0 : 3) }
        }
        center.getNotificationSettings { settings in
            if queryOnly != 0 { reply(identifier, 100 + Int32(settings.authorizationStatus.rawValue)); return }
            switch settings.authorizationStatus {
            case .notDetermined:
                guard explicitTest != 0 else { reply(identifier, 2); return }
                center.requestAuthorization(options: [.alert, .sound]) { allowed, error in
                    if allowed && error == nil { submit() } else { reply(identifier, error == nil ? 1 : 3) }
                }
            case .denied: reply(identifier, 1)
            case .authorized, .provisional: submit()
            @unknown default: reply(identifier, 3)
            }
        }
    }
}
