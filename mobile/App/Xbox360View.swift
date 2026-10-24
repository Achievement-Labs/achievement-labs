import SwiftUI
import WebKit
import UniformTypeIdentifiers
import CryptoKit
import LabsMobileCore

struct Xbox360View: View {
    @EnvironmentObject var model: MobileModel
    var body: some View { NavigationStack { Xbox360WebView(model:model).navigationTitle("Xbox 360").navigationBarTitleDisplayMode(.inline) } }
}

private struct Xbox360WebView: UIViewRepresentable {
    let model: MobileModel
    func makeCoordinator() -> Coordinator { Coordinator(model:model) }
    func makeUIView(context:Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        config.userContentController.add(context.coordinator,name:"labs360")
        let web = WKWebView(frame:.zero,configuration:config)
        web.isOpaque = false; web.backgroundColor = .black
        web.navigationDelegate = context.coordinator; context.coordinator.web = web
        if let folder = Bundle.main.resourceURL?.appendingPathComponent("Shared360",isDirectory:true) {
            context.coordinator.folder = folder
            web.loadFileURL(folder.appendingPathComponent("index.html"),allowingReadAccessTo:folder)
        }
        return web
    }
    func updateUIView(_ uiView:WKWebView,context:Context) {}
    static func dismantleUIView(_ uiView:WKWebView,coordinator:Coordinator) { uiView.configuration.userContentController.removeScriptMessageHandler(forName:"labs360"); uiView.navigationDelegate = nil }

    @MainActor final class Coordinator: NSObject,WKScriptMessageHandler,WKNavigationDelegate,UIDocumentPickerDelegate {
        weak var web: WKWebView?
        let model: MobileModel
        var folder: URL?
        var original = Data(), working = Data(), pending: Data?
        var expected = 0, digest = "", directory: URL?
        var pickerID: String?, pickerAction = ""
        init(model:MobileModel) { self.model = model }
        func hash(_ data:Data) -> String { Insecure.SHA1.hash(data:data).map { String(format:"%02x",$0) }.joined() }
        func reply(_ id:String,_ result:Any = [:],error:String? = nil) {
            guard let web else { return }
            let envelope:[Any] = [id,result,error as Any? ?? NSNull()]
            guard let data = try? JSONSerialization.data(withJSONObject:envelope),let json = String(data:data,encoding:.utf8) else { return }
            web.evaluateJavaScript("window.native360Result(...\(json))",completionHandler:nil)
        }
        func userContentController(_ userContentController:WKUserContentController,didReceive message:WKScriptMessage) {
            guard message.frameInfo.isMainFrame, message.frameInfo.request.url?.isFileURL == true,
                  let path = message.frameInfo.request.url?.standardizedFileURL.path, let folder,
                  path == folder.appendingPathComponent("index.html").standardizedFileURL.path,
                  let body = message.body as? [String:Any],let id = body["id"] as? String,id.count <= 32,
                  let action = body["action"] as? String,let p = body["data"] as? [String:Any] else { return }
            Task { do { if let result = try await handle(action,p,id:id) { reply(id,result) } } catch { reply(id,error:error.localizedDescription) } }
        }
        func webView(_ webView:WKWebView,decidePolicyFor navigationAction:WKNavigationAction,decisionHandler:@escaping (WKNavigationActionPolicy)->Void) {
            let allowed = navigationAction.request.url?.standardizedFileURL == folder?.appendingPathComponent("index.html").standardizedFileURL
            decisionHandler(allowed ? .allow : .cancel)
        }
        func webView(_ webView:WKWebView,didFinish navigation:WKNavigation!) {
            #if DEBUG
            if ProcessInfo.processInfo.arguments.contains("--ui-preview=xbox360") { webView.evaluateJavaScript("window.mobile360SelfTest()",completionHandler:nil) }
            #endif
        }
        func fail(_ text:String) -> LabsError { .message(text) }
        func privateWrite(_ data:Data,to url:URL) throws {
            try data.write(to:url,options:[.atomic,.completeFileProtectionUntilFirstUserAuthentication])
            guard try Data(contentsOf:url) == data else { throw fail("Private profile readback failed.") }
        }
        func verifiedExport(_ backup:Bool) throws -> URL {
            guard let directory, pending == nil,!working.isEmpty else { throw fail("No completed profile is available.") }
            let file = directory.appendingPathComponent(backup ? "original.con" : "working.con")
            guard try Data(contentsOf:file) == (backup ? original : working) else { throw fail("Private profile changed. Reload before export.") }
            return file
        }
        func handle(_ action:String,_ p:[String:Any],id:String) async throws -> Any? {
            switch action {
            #if DEBUG
            case "testProfile":
                guard ProcessInfo.processInfo.arguments.contains("--ui-preview=xbox360"),let folder else { throw fail("Test mode is unavailable.") }
                let bytes = try Data(contentsOf:folder.appendingPathComponent("synthetic.dat"))
                let next = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString,isDirectory:true)
                try FileManager.default.createDirectory(at:next,withIntermediateDirectories:true)
                try privateWrite(bytes,to:next.appendingPathComponent("original.con")); try privateWrite(bytes,to:next.appendingPathComponent("working.con"))
                original = bytes; working = bytes; directory = next; return ["length":bytes.count]
            case "testDone":
                guard ProcessInfo.processInfo.arguments.contains("--ui-preview=xbox360") else { throw fail("Test mode is unavailable.") }
                if p["passed"] as? Bool == true {
                    guard try Data(contentsOf:verifiedExport(true)) != Data(contentsOf:verifiedExport(false)) else { throw fail("Test output unchanged.") }
                }
                let file = FileManager.default.urls(for:.documentDirectory,in:.userDomainMask)[0].appendingPathComponent("Xbox360-self-test.json")
                try JSONSerialization.data(withJSONObject:p).write(to:file,options:.atomic); return [:]
            #endif
            case "pickProfile","pickReference","export":
                guard pickerID == nil,let web,let controller = web.window?.rootViewController else { throw fail("A document picker is already open or unavailable.") }
                let picker:UIDocumentPickerViewController
                if action == "export" { picker = UIDocumentPickerViewController(forExporting:[try verifiedExport(p["original"] as? Bool ?? false)],asCopy:true) }
                else { picker = UIDocumentPickerViewController(forOpeningContentTypes:[.data,.item],asCopy:true) }
                pickerID = id; pickerAction = action; picker.delegate = self
                var presenter = controller; while let next = presenter.presentedViewController { presenter = next }
                presenter.present(picker,animated:true); return nil
            case "identity":
                guard let folder else { throw fail("Signing assets are missing.") }
                let bytes = try Data(contentsOf:folder.appendingPathComponent("signing.dat"))
                guard bytes.count == 556 else { throw fail("Signing assets are invalid.") }
                return ["base64":bytes.base64EncodedString()]
            case "readChunk":
                guard let offset = p["offset"] as? Int,offset >= 0,offset < working.count else { throw fail("Invalid profile chunk.") }
                return ["base64":working.subdata(in:offset..<min(offset+32768,working.count)).base64EncodedString()]
            case "beginSave":
                guard let directory,pending == nil, let count = p["length"] as? Int,count == working.count,count <= 32*1024*1024,
                      let digest = p["sha1"] as? String,digest.range(of:"^[0-9a-f]{40}$",options:.regularExpression) != nil,
                      p["before"] as? String == hash(working),try Data(contentsOf:directory.appendingPathComponent("working.con")) == working else { throw fail("Working profile changed or save is invalid. Reload before editing.") }
                expected = count; self.digest = digest; pending = Data(); return [:]
            case "saveChunk":
                guard let offset = p["offset"] as? Int,let encoded = p["base64"] as? String,encoded.count <= 44000,let chunk = Data(base64Encoded:encoded),!chunk.isEmpty,chunk.count <= 32768,let current = pending,offset == current.count,offset+chunk.count <= expected else { throw fail("Invalid or out-of-order save chunk.") }
                pending!.append(chunk); return [:]
            case "commitSave":
                guard let directory,let next = pending,next.count == expected,hash(next) == digest else { throw fail("Signed profile transfer failed verification.") }
                try privateWrite(working,to:directory.appendingPathComponent("before-save-\(UUID().uuidString).con"))
                try privateWrite(next,to:directory.appendingPathComponent("working.con")); working = next; pending = nil; return [:]
            case "abortSave": pending = nil; return [:]
            case "art":
                let title = p["titleId"] as? String ?? ""
                let game = model.games.first { $0.id == title } ?? model.titleDetails[title]
                return ["url":game?.image ?? ""]
            case "reference":
                guard let account = model.account else { throw fail("Connect an Xbox account in Settings first.") }
                return ["rows":try await model.api.reference360(p["titleId"] as? String ?? "",gamertag:p["gamertag"] as? String ?? "",account:account)]
            default: throw fail("Unsupported Xbox 360 command.")
            }
        }
        func documentPickerWasCancelled(_ controller:UIDocumentPickerViewController) { if let id = pickerID { pickerID = nil; reply(id,["cancelled":true]) } }
        func documentPicker(_ controller:UIDocumentPickerViewController,didPickDocumentsAt urls:[URL]) {
            guard let id = pickerID else { return }; pickerID = nil
            do {
                guard let url = urls.first else { reply(id,["cancelled":true]); return }
                if pickerAction == "export" { reply(id); return }
                let scoped = url.startAccessingSecurityScopedResource(); defer { if scoped { url.stopAccessingSecurityScopedResource() } }
                let max = pickerAction == "pickReference" ? 2*1024*1024 : 32*1024*1024
                let size = try url.resourceValues(forKeys:[.fileSizeKey]).fileSize ?? Int.max
                guard size > 0,size <= max else { throw fail("Document exceeds the size limit.") }
                let bytes = try Data(contentsOf:url); guard bytes.count == size else { throw fail("Document changed while importing.") }
                if pickerAction == "pickReference" { guard let text = String(data:bytes,encoding:.utf8) else { throw fail("Reference file must use UTF-8.") };reply(id,["text":text]);return }
                guard pending == nil else { throw fail("Finish the current save first.") }
                var root = FileManager.default.urls(for:.applicationSupportDirectory,in:.userDomainMask)[0].appendingPathComponent("Xbox360",isDirectory:true)
                try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
                var values = URLResourceValues(); values.isExcludedFromBackup = true; try root.setResourceValues(values)
                let next = root.appendingPathComponent(UUID().uuidString,isDirectory:true); try FileManager.default.createDirectory(at:next,withIntermediateDirectories:true)
                try privateWrite(bytes,to:next.appendingPathComponent("original.con"));try privateWrite(bytes,to:next.appendingPathComponent("working.con"))
                original = bytes; working = bytes; directory = next;reply(id,["length":bytes.count])
            } catch { reply(id,error:error.localizedDescription) }
        }
    }
}
