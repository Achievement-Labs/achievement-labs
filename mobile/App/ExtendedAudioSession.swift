import Foundation
import Combine
import AVFAudio

/// Explicit, finite compatibility mode for sideloaded use. Never starts at launch.
@MainActor final class ExtendedAudioSession: ObservableObject {
    @Published private(set) var active = false
    @Published private(set) var status = "Extended audio is off."
    private var player: AVAudioPlayer?
    var playing: Bool { player?.isPlaying == true }
    private var observer: NSObjectProtocol?
    private var interrupted: ((String) -> Void)?
    func start(interrupted: @escaping (String) -> Void) throws {
        guard !active else { return }
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playback,mode:.default,options:[.mixWithOthers])
        try session.setActive(true)
        do {
            // One second of silent PCM, looped locally; no microphone or network.
            var data = Data()
            func word<T: FixedWidthInteger>(_ value: T) { var v = value.littleEndian; withUnsafeBytes(of:&v) { data.append(contentsOf:$0) } }
            data.append(Data("RIFF".utf8)); word(UInt32(36+16000)); data.append(Data("WAVEfmt ".utf8))
            word(UInt32(16)); word(UInt16(1)); word(UInt16(1)); word(UInt32(8000)); word(UInt32(16000)); word(UInt16(2)); word(UInt16(16))
            data.append(Data("data".utf8)); word(UInt32(16000)); data.append(Data(repeating:0,count:16000))
            let next = try AVAudioPlayer(data:data); next.numberOfLoops = -1; next.volume = 0
            guard next.prepareToPlay(), next.play() else { throw NSError(domain:"LabsAudio",code:1) }
            player = next; self.interrupted = interrupted; active = true
            status = "Extended audio active · finite session · interruptions can stop it."
            observer = NotificationCenter.default.addObserver(forName:AVAudioSession.interruptionNotification,object:session,queue:.main) { [weak self] note in
                guard let kind = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt, kind == AVAudioSession.InterruptionType.began.rawValue else { return }
                Task { @MainActor [weak self] in
                    guard let self, self.active else { return }; let action = self.interrupted
                    self.stop(); self.status = "Extended audio interrupted. Resume manually."; action?(self.status)
                }
            }
        } catch { try? session.setActive(false,options:.notifyOthersOnDeactivation); throw error }
    }
    func stop() {
        guard active || player != nil else { return }
        player?.stop(); player = nil; active = false; interrupted = nil
        if let observer { NotificationCenter.default.removeObserver(observer); self.observer = nil }
        try? AVAudioSession.sharedInstance().setActive(false,options:.notifyOthersOnDeactivation)
        status = "Extended audio stopped."
    }
}
