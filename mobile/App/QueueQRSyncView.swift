import SwiftUI
import CoreImage.CIFilterBuiltins
import LabsMobileCore

struct QueueQRSyncView: View {
    @EnvironmentObject var m: MobileModel
    @Environment(\.dismiss) private var dismiss
    @State private var frames: [String] = []
    @State private var index = 0
    @State private var cycling = true
    @State private var scanning = false
    @State private var pending: Data?
    @State private var summary = ""
    @State private var message = ""
    @State private var share: [URL] = []
    @State private var sharing = false
    @State private var folder: URL?
    private let timer = Timer.publish(every:0.5,on:.main,in:.common).autoconnect()
    static func image(_ frame: String) -> UIImage? {
        let filter = CIFilter.qrCodeGenerator(); filter.message = Data(frame.utf8); filter.correctionLevel = "M"
        guard let output = filter.outputImage, let cg = CIContext().createCGImage(output.transformed(by:CGAffineTransform(scaleX:6,y:6)),from:output.extent.applying(CGAffineTransform(scaleX:6,y:6))) else { return nil }
        return UIImage(cgImage:cg)
    }
    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment:.leading,spacing:16) {
                    Button("Show my saved queues as QR",systemImage:"qrcode") {
                        do { guard let owner = m.account?.xuid else { return }; frames = try QueueQRTransfer.frames(m.exportQueueTransfer(),owner:owner); index = 0; message = "" }
                        catch { message = error.localizedDescription }
                    }.disabled(m.busy || m.running || m.account == nil)
                    Button("Scan queue QR",systemImage:"qrcode.viewfinder") { scanning = true }.disabled(m.busy || m.running || m.account == nil)
                    if !frames.isEmpty {
                        if let image = Self.image(frames[index]) { Image(uiImage:image).interpolation(.none).resizable().scaledToFit().frame(maxWidth:410).background(.white).accessibilityLabel("Queue QR code \(index+1)") }
                        Text("Code \(index+1) of \(frames.count)").monospacedDigit()
                        Toggle("Cycle automatically",isOn:$cycling)
                        HStack { Button("Previous") { index = (index+frames.count-1)%frames.count }; Spacer(); Button("Next") { index = (index+1)%frames.count } }
                        Button("Share all QR images") { shareImages() }
                    }
                    if let data = pending {
                        Text(summary).font(.headline)
                        Text("Matching saved queues will be replaced after Xbox verifies their achievements. Nothing starts automatically.").font(.caption)
                        Button("Import saved queues") { pending = nil; Task { await m.importQueueTransfer(data); message = m.error ?? m.status } }.disabled(m.busy || m.running)
                        Button("Cancel import",role:.cancel) { pending = nil }
                    }
                    if !message.isEmpty { Text(message).font(.caption) }
                    Text("Use the same Xbox account on both devices. These codes include saved game queues, progress and delays, without sign-in or event tokens. On PC, choose Import queue QR images and select all the shared PNGs together. Large transfers can use the existing sync file.").font(.caption).foregroundStyle(.secondary)
                }.padding()
            }.navigationTitle("Queue QR sync").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement:.cancellationAction) { Button("Done") { dismiss() }.disabled(m.busy) } }
            .onReceive(timer) { _ in if cycling && !frames.isEmpty && !scanning && !sharing { index = (index+1)%frames.count } }
            .sheet(isPresented:$scanning) { PCQRScanner(queueOnly:true) { data,_ in
                scanning = false
                do { guard let owner = m.account?.xuid else { return }; let transfer = try JSONDecoder().decode(QueueTransfer.self,from:data); try transfer.validate(owner:owner); pending = data; summary = "\(transfer.games.count) games · \(transfer.games.reduce(0) { $0+$1.items.count }) achievements"; message = "" }
                catch { message = error.localizedDescription }
            } }
            .sheet(isPresented:$sharing) { QueueQRShare(items:share) }
        }.onDisappear { if let folder { try? FileManager.default.removeItem(at:folder) } }
    }
    private func shareImages() {
        do {
            if let folder { try? FileManager.default.removeItem(at:folder) }
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("Labs-Queue-"+UUID().uuidString)
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true); folder = directory
            var urls: [URL] = []
            for (i,frame) in frames.enumerated() {
                guard let data = Self.image(frame)?.pngData() else { throw LabsError.message("Could not render a QR image.") }
                let url = directory.appendingPathComponent(String(format:"Queue-%03d.png",i+1)); try data.write(to:url); urls.append(url)
            }
            share = urls; sharing = true
        } catch { message = error.localizedDescription }
    }
}
private struct QueueQRShare: UIViewControllerRepresentable {
    let items: [URL]
    func makeUIViewController(context:Context) -> UIActivityViewController { UIActivityViewController(activityItems:items,applicationActivities:nil) }
    func updateUIViewController(_ controller:UIActivityViewController,context:Context) {}
}
