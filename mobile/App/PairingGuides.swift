import SwiftUI

struct PairingGuides: View {
    @EnvironmentObject var m: MobileModel
    var scan: () -> Void
    var body: some View {
        Section("PC → iPhone / iPad spoofing") {
            Text("1. On Windows, sign in to the Xbox PC app and attach Achievement Labs to it.")
            Text("2. Windows: Settings → Sync → Show mobile QR code. Choose Sign-in only, or Sign-in + event token if you also need the event token.")
            Text("3. Stop active mobile sessions, then tap Scan QR below. Keep the camera on the cycling codes until every frame is collected.")
            Text("4. Check the displayed gamertag and tap Connect this PC account.")
            Text("5. Open Game Session, select your game and start spoofing.")
            Button("Scan QR", systemImage: "qrcode.viewfinder", action: scan)
                .disabled(m.busy || m.running || m.presenceRunning)
            Text("QR transfer does not need the same Wi-Fi network. Event-token-only import leaves your sign-in unchanged and does not enable spoofing.")
                .font(.caption).foregroundStyle(.secondary)
        }.listRowBackground(LabsGlassSurface())
        Section("PC → Mac") {
            DisclosureGroup("Pair on the same network") {
                Text("1. Connect the same Xbox account in both apps. Windows must attach to the signed-in Xbox PC app.")
                Text("2. Windows: Settings → Sync → Pair a Mac on this network.")
                Text("3. Mac: Settings → Sync → Pair with Windows on this network. Select the PC and click Pair selected PC.")
                Text("4. Compare the code and approve on both devices. Select Remember this device on both for future recognition.")
                Text("5. Mac: open Game Session and start spoofing. If discovery misses the PC, enter the address and port shown on Windows.")
            }
        }.listRowBackground(LabsGlassSurface())
        Section("When to pair again") {
            Text("Mobile QR codes and exported files must be imported within 10 minutes. This is a transfer deadline, separate from the Xbox authorization lifetime.")
            Text("If Xbox refuses or the imported authorization expires, generate fresh sign-in codes on Windows and scan again. A remembered Microsoft browser sign-in or an event token does not renew PC-issued spoofing authorization.")
            Text("Mobile background limits still apply. Start sessions yourself; importing never starts spoofing or an unlock queue.")
            Text("Keep sign-in QR codes, transfer files and pairing keys private.").font(.caption).foregroundStyle(.secondary)
        }.listRowBackground(LabsGlassSurface())
    }
}
