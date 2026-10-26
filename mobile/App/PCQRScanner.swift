import SwiftUI
import AVFoundation
import LabsMobileCore

struct PCQRScanner: View {
    var queueOnly = false
    let completion: (Data,String) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var collector = QRTransferCollector()
    @State private var status = "Point the camera at the transfer codes."
    @State private var cameraError: String?
    @State private var finished = false
    var body: some View {
        NavigationStack {
            VStack(spacing:16) {
                Text("Keep the QR codes in view while they cycle.").font(.headline)
                CameraReader(onCode: { frame in
                    guard !finished else { return }
                    do {
                        if let (data,key) = try collector.accept(frame) { finished = true; completion(data,key) }
                        else { status = collector.progress }
                    } catch { status = error.localizedDescription }
                },onError: { cameraError = $0 }).clipShape(RoundedRectangle(cornerRadius:16))
                Text(cameraError ?? status).font(.subheadline).multilineTextAlignment(.center)
                if cameraError != nil, let url = URL(string:UIApplication.openSettingsURLString) { Link("Open camera permission settings",destination:url) }
                Button("Reset scanner") { collector = QRTransferCollector(queueOnly:queueOnly,autoDetect:true); status = collector.progress }
                Text(queueOnly ? "Queue codes contain saved progress and delays, without sign-in tokens." : "Recognises sign-in, event-token and saved queue codes. Only scan your own transfers; sign-in codes contain private credentials.").font(.caption).foregroundStyle(.secondary)
            }.padding().onAppear { collector = QRTransferCollector(queueOnly:queueOnly,autoDetect:true) }.navigationTitle(queueOnly ? "Scan queue codes" : "Scan QR codes").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement:.cancellationAction) { Button("Cancel") { dismiss() } } }
        }
    }
}
private final class CameraSurface: UIView {
    let preview = AVCaptureVideoPreviewLayer()
    override init(frame:CGRect) { super.init(frame:frame); preview.videoGravity = .resizeAspectFill; layer.addSublayer(preview) }
    required init?(coder:NSCoder) { fatalError("init(coder:) is unavailable") }
    override func layoutSubviews() { super.layoutSubviews(); preview.frame = bounds }
}
private struct CameraReader: UIViewRepresentable {
    let onCode: (String)->Void
    let onError: (String)->Void
    func makeCoordinator() -> Coordinator { Coordinator(onCode:onCode,onError:onError) }
    func makeUIView(context:Context) -> CameraSurface {
        let view = CameraSurface(); view.preview.session = context.coordinator.session
        context.coordinator.start(); return view
    }
    func updateUIView(_ view:CameraSurface,context:Context) {}
    static func dismantleUIView(_ view:CameraSurface,coordinator:Coordinator) { view.preview.session = nil; coordinator.stop() }
    final class Coordinator: NSObject, AVCaptureMetadataOutputObjectsDelegate {
        let session = AVCaptureSession()
        private let queue = DispatchQueue(label:"AchievementLabs.QRScanner")
        private var stopped = false
        let onCode:(String)->Void; let onError:(String)->Void
        init(onCode:@escaping(String)->Void,onError:@escaping(String)->Void) { self.onCode=onCode; self.onError=onError }
        func start() {
            switch AVCaptureDevice.authorizationStatus(for:.video) {
            case .authorized: configure()
            case .notDetermined: AVCaptureDevice.requestAccess(for:.video) { allowed in if allowed { self.configure() } else { self.fail("Camera access is disabled. Enable it in Settings or use the file fallback.") } }
            default: fail("Camera access is disabled. Enable it in Settings or use the file fallback.")
            }
        }
        func fail(_ text:String) { DispatchQueue.main.async { self.onError(text) } }
        func configure() { queue.async {
            guard !self.stopped else { return }
            do {
                guard let camera = AVCaptureDevice.default(for:.video) else { throw LabsError.message("No camera is available.") }
                let input = try AVCaptureDeviceInput(device:camera); let output = AVCaptureMetadataOutput()
                self.session.beginConfiguration()
                guard self.session.canAddInput(input),self.session.canAddOutput(output) else { self.session.commitConfiguration(); throw LabsError.message("Camera scanning is unavailable.") }
                self.session.addInput(input); self.session.addOutput(output)
                output.setMetadataObjectsDelegate(self,queue:.main); output.metadataObjectTypes = [.qr]
                self.session.commitConfiguration(); self.session.startRunning()
            } catch { self.fail(error.localizedDescription) }
        } }
        func stop() { queue.async { self.stopped = true; if self.session.isRunning { self.session.stopRunning() } } }
        func metadataOutput(_ output:AVCaptureMetadataOutput,didOutput objects:[AVMetadataObject],from connection:AVCaptureConnection) {
            for object in objects { if let code = object as? AVMetadataMachineReadableCodeObject, let value = code.stringValue { onCode(value) } }
        }
    }
}

