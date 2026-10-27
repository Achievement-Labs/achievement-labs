import AppKit
import WebKit

// The callback is returned only over the parent process's anonymous stdout pipe.
// Never log a navigation URL, credential, cookie or authorization code.
enum CallbackGate {
    static func isCallback(_ url: URL) -> Bool {
        url.scheme?.lowercased() == "https" && url.host?.lowercased() == "login.live.com" &&
        url.path == "/oauth20_desktop.srf" && (url.port == nil || url.port == 443) &&
        url.user == nil && url.password == nil && url.fragment == nil
    }
    static func valid(_ url: URL, state: String) -> Bool {
        guard isCallback(url), let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems else { return false }
        let states = items.filter { $0.name == "state" }, codes = items.filter { $0.name == "code" }
        return !state.isEmpty && states.count == 1 && states[0].value == state && codes.count == 1 &&
            !(codes[0].value ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
            !items.contains { $0.name == "error" }
    }
}

@MainActor
final class SignInWindow: NSObject, NSApplicationDelegate, NSWindowDelegate, WKNavigationDelegate, WKUIDelegate {
    var window: NSWindow!
    var web: WKWebView!
    let start: URL?
    let state: String
    let selfTest: Bool
    var finished = false
    init(start: URL?, state: String, selfTest: Bool) {
        self.start = start; self.state = state; self.selfTest = selfTest
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        config.mediaTypesRequiringUserActionForPlayback = .all
        config.preferences.javaScriptCanOpenWindowsAutomatically = true
        web = WKWebView(frame: .zero, configuration: config)
        web.navigationDelegate = self; web.uiDelegate = self
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 720),
                          styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "Achievement Labs — Microsoft sign-in"
        window.delegate = self; window.isReleasedWhenClosed = false
        window.contentView = web; window.center(); window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        if selfTest {
            // Synthetic navigation is cancelled before any request reaches Microsoft.
            web.loadHTMLString("<script>location.href='https://login.live.com/oauth20_desktop.srf?code=synthetic&state=synthetic-state'</script>", baseURL: nil)
        } else if let start { web.load(URLRequest(url: start)) }
        DispatchQueue.main.asyncAfter(deadline: .now() + (selfTest ? 30 : 900)) { [weak self] in self?.finish("timeout") }
    }
    func finish(_ status: String, redirect: URL? = nil) {
        guard !finished else { return }; finished = true
        var result = ["status": status]
        if let redirect, !selfTest { result["redirect"] = redirect.absoluteString }
        if let data = try? JSONSerialization.data(withJSONObject: result) {
            FileHandle.standardOutput.write(data); FileHandle.standardOutput.write(Data([10]))
        }
        web?.stopLoading()
        NSApp.terminate(nil)
    }
    func windowWillClose(_ notification: Notification) { finish("cancelled") }
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url else { decisionHandler(.cancel); return }
        if CallbackGate.isCallback(url) {
            decisionHandler(.cancel)
            guard action.targetFrame?.isMainFrame != false, CallbackGate.valid(url, state: state) else { finish("invalidCallback"); return }
            finish(selfTest ? "selfTestPassed" : "completed", redirect: url)
            return
        }
        if selfTest && url.scheme == "https" { decisionHandler(.cancel); finish("selfTestFailed"); return }
        guard url.scheme == "https" || url.absoluteString == "about:blank" else { decisionHandler(.cancel); return }
        // Bring HTTPS authentication popups into this same ephemeral sign-in window.
        if action.targetFrame == nil { decisionHandler(.cancel); webView.load(action.request); return }
        decisionHandler(.allow)
    }
    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        if (error as NSError).code != NSURLErrorCancelled { finish("loadFailed") }
    }
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) { finish("loadFailed") }
}

@main
struct MicrosoftSignInApp {
    @MainActor
    static func main() {
        let selfTest = CommandLine.arguments.contains("--self-test")
        var start: URL?, state = "synthetic-state"
        if selfTest {
            let good = "https://login.live.com/oauth20_desktop.srf?code=synthetic&state=synthetic-state"
            guard CallbackGate.valid(URL(string: good)!, state: state) else { exit(1) }
            for bad in [good + "&code=duplicate", good + "&state=duplicate", good + "&error=denied", good + "#fragment",
                        good.replacingOccurrences(of: "login.live.com", with: "example.com"),
                        good.replacingOccurrences(of: "https:", with: "http:"),
                        good.replacingOccurrences(of: "synthetic-state", with: "wrong-state"),
                        "https://login.live.com/oauth20_desktop.srf?removed=true"] {
                guard !CallbackGate.valid(URL(string: bad)!, state: state) else { exit(1) }
            }
        } else {
            guard let line = readLine(), line.utf8.count <= 16384, let data = line.data(using: .utf8),
                  let input = try? JSONSerialization.jsonObject(with: data) as? [String:String],
                  let raw = input["startUri"], let url = URL(string: raw),
                  url.scheme == "https", url.host == "login.live.com", url.path == "/oauth20_authorize.srf",
                  url.user == nil, url.password == nil, url.fragment == nil, (url.port == nil || url.port == 443),
                  let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems else { exit(1) }
            let states = items.filter { $0.name == "state" }
            guard states.count == 1, let value = states[0].value, value.count >= 16,
                  items.filter({ $0.name == "redirect_uri" }).map(\.value) == ["https://login.live.com/oauth20_desktop.srf"],
                  items.filter({ $0.name == "code_challenge_method" }).map(\.value) == ["S256"],
                  items.contains(where: { $0.name == "code_challenge" && ($0.value?.count ?? 0) >= 43 }) else { exit(1) }
            start = url; state = value
        }
        let application = NSApplication.shared
        application.setActivationPolicy(.accessory)
        let delegate = SignInWindow(start: start, state: state, selfTest: selfTest)
        application.delegate = delegate
        withExtendedLifetime(delegate) { application.run() }
    }
}
