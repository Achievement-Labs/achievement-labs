import SwiftUI
import WebKit
import LabsMobileCore

struct SignInPage: Identifiable { let id = UUID(); let url: URL }
@MainActor final class SignInWebState: ObservableObject {
    @Published var host = "login.live.com"
    @Published var loading = true
    @Published var failed = false
}
struct EmbeddedSignIn: View {
    let url: URL
    let onComplete: (String) -> Void
    let onCancel: () -> Void
    var remember: Bool = true
    @StateObject private var state = SignInWebState()
    var body: some View {
        NavigationStack {
            VStack(spacing:0) {
                HStack(spacing:6) { Image(systemName:"lock.fill"); Text(state.host).lineLimit(1); Spacer(); if state.loading { ProgressView() } }.font(.caption).padding(10).background(.thinMaterial)
                if state.failed { Text("The sign-in page could not load. Cancel and use Safari / manual sign-in in Settings.").font(.caption).padding() }
                SignInWebView(url:url,state:state,onComplete:onComplete,remember:remember)
            }.navigationTitle("Microsoft sign-in").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement:.cancellationAction) { Button("Cancel",action:onCancel) } }
        }
    }
}
/// No scripts or page contents are read. Only the registered top-level OAuth redirect is intercepted.
struct SignInWebView: UIViewRepresentable {
    let url: URL
    @ObservedObject var state: SignInWebState
    let onComplete: (String) -> Void
    var remember: Bool = true
    func makeCoordinator() -> Coordinator { Coordinator(self) }
    func makeUIView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration(); config.websiteDataStore = remember ? .default() : .nonPersistent()
        config.allowsInlineMediaPlayback = true
        config.mediaTypesRequiringUserActionForPlayback = .all
        config.allowsPictureInPictureMediaPlayback = false
        config.allowsAirPlayForMediaPlayback = false
        let view = WKWebView(frame:.zero,configuration:config)
        view.navigationDelegate = context.coordinator; view.uiDelegate = context.coordinator
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains("--web-callback-self-test") {
            let policy: [String:Bool] = ["inline":view.configuration.allowsInlineMediaPlayback,"requiresTap":view.configuration.mediaTypesRequiringUserActionForPlayback == .all,"pictureInPicture":view.configuration.allowsPictureInPictureMediaPlayback,"airPlay":view.configuration.allowsAirPlayForMediaPlayback]
            let marker = FileManager.default.urls(for:.documentDirectory,in:.userDomainMask)[0].appendingPathComponent("SignInMedia-self-test.json")
            do { try JSONSerialization.data(withJSONObject:policy).write(to:marker,options:.atomic) } catch { fatalError("Media policy self-test could not save its result") }
            view.loadHTMLString("<html><head><meta http-equiv='refresh' content='0;url=https://login.live.com/oauth20_desktop.srf?state=synthetic-state&amp;code=synthetic-code'></head><body>Synthetic callback check</body></html>",baseURL:nil)
            return view
        }
        #endif
        view.load(URLRequest(url:url)); return view
    }
    func updateUIView(_ uiView: WKWebView, context: Context) { context.coordinator.parent = self }
    static func dismantleUIView(_ uiView: WKWebView, coordinator: Coordinator) { uiView.stopLoading(); uiView.navigationDelegate = nil; uiView.uiDelegate = nil }
    final class Coordinator: NSObject, WKNavigationDelegate, WKUIDelegate {
        var parent: SignInWebView
        private var completed = false
        init(_ parent: SignInWebView) { self.parent = parent }
        func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            guard !completed, let url = action.request.url else { decisionHandler(.cancel); return }
            if action.targetFrame?.isMainFrame != false && url.scheme == "https" && url.host?.lowercased() == "login.live.com" && url.path == "/oauth20_desktop.srf" {
                completed = true; decisionHandler(.cancel); webView.stopLoading()
                // BrowserLogin performs full state, code, host, path, port and one-use validation.
                DispatchQueue.main.async { self.parent.onComplete(url.absoluteString) }; return
            }
            guard url.scheme == "https" || url.absoluteString == "about:blank" else { decisionHandler(.cancel); return }
            if action.targetFrame?.isMainFrame != false {
                DispatchQueue.main.async { self.parent.state.host = url.host ?? "Microsoft sign-in"; self.parent.state.loading = true; self.parent.state.failed = false }
            }
            decisionHandler(.allow)
        }
        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { parent.state.loading = false }
        func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { fail(error) }
        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { fail(error) }
        private func fail(_ error: Error) { if !completed && (error as NSError).code != NSURLErrorCancelled { parent.state.loading = false; parent.state.failed = true } }
        func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
            if action.targetFrame == nil, action.request.url?.scheme == "https" { webView.load(action.request) }; return nil
        }
    }
}

#if DEBUG
struct CallbackCaptureTestView: View {
    @StateObject private var state = SignInWebState()
    @State private var passed = false
    var body: some View {
        VStack {
            Text(passed ? "Automatic callback capture passed" : "Checking automatic callback capture…")
            SignInWebView(url:URL(string:"https://login.live.com/")!,state:state) { redirect in
                do {
                    guard try BrowserLogin.validateRedirect(redirect,state:"synthetic-state") == "synthetic-code" else { fatalError("Synthetic callback mismatch") }
                    let marker = FileManager.default.urls(for:.documentDirectory,in:.userDomainMask)[0].appendingPathComponent("Callback-self-test.txt")
                    try Data("passed".utf8).write(to:marker,options:.atomic); passed = true
                } catch { fatalError("Synthetic callback validation failed") }
            }
        }.background(Color.black)
    }
}
#endif
