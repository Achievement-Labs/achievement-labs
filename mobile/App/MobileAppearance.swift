import SwiftUI
import UIKit
import ImageIO
import UniformTypeIdentifiers
import LabsMobileCore

@MainActor final class MobileAppearance: ObservableObject {
    @Published var glass = UserDefaults.standard.object(forKey:"appearance.glass") as? Bool ?? true { didSet { save("glass",glass) } }
    @Published var oled = UserDefaults.standard.bool(forKey:"appearance.oled") { didSet { save("oled",oled) } }
    @Published var transparency = UserDefaults.standard.object(forKey:"appearance.transparency") as? Double ?? 0.16 { didSet { save("transparency",transparency) } }
    @Published var blur = UserDefaults.standard.object(forKey:"appearance.blur") as? Double ?? 0.6 { didSet { save("blur",blur) } }
    @Published var tint = UserDefaults.standard.string(forKey:"appearance.tint") ?? "34594C" { didSet { save("tint",tint) } }
    @Published var accent = UserDefaults.standard.string(forKey:"appearance.accent") ?? "80E69E" { didSet { save("accent",accent) } }
    @Published var background = UserDefaults.standard.string(forKey:"appearance.background") ?? "game" { didSet { save("background",background) } }
    @Published var persistent = UserDefaults.standard.object(forKey:"appearance.persistent") as? Bool ?? true { didSet { save("persistent",persistent) } }
    @Published var fit = UserDefaults.standard.bool(forKey:"appearance.fit") { didSet { save("fit",fit) } }
    @Published var dim = UserDefaults.standard.object(forKey:"appearance.dim") as? Double ?? 0.45 { didSet { save("dim",dim) } }
    @Published var artBlur = UserDefaults.standard.double(forKey:"appearance.artBlur") { didSet { save("artBlur",artBlur) } }
    @Published var title = UserDefaults.standard.string(forKey:"appearance.title") ?? "" { didSet { save("title",title) } }
    @Published var customRevision = 0
    private func save(_ key:String,_ value:Any) { UserDefaults.standard.set(value,forKey:"appearance."+key) }
    var accentColor: Color { Color(hex:accent) }
    var tintColor: Color { Color(hex:tint) }
    static var customURL: URL { FileManager.default.urls(for:.applicationSupportDirectory,in:.userDomainMask)[0].appendingPathComponent("LabsCustomBackground.jpg") }
    func importBackground(_ url:URL) throws {
        let access = url.startAccessingSecurityScopedResource(); defer { if access { url.stopAccessingSecurityScopedResource() } }
        guard try url.resourceValues(forKeys:[.fileSizeKey]).fileSize ?? 0 <= 16*1024*1024 else { throw LabsError.message("Background image exceeds 16 MB.") }
        guard let image = Self.downsample(try Data(contentsOf:url)), let bytes = image.jpegData(compressionQuality:0.88) else { throw LabsError.message("Choose a supported image file.") }
        try FileManager.default.createDirectory(at:Self.customURL.deletingLastPathComponent(),withIntermediateDirectories:true)
        try bytes.write(to:Self.customURL,options:[.atomic,.completeFileProtectionUntilFirstUserAuthentication]); customRevision += 1; background = "custom"
    }
    static func downsample(_ data:Data) -> UIImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData,nil), let image = CGImageSourceCreateThumbnailAtIndex(source,0,[kCGImageSourceCreateThumbnailFromImageAlways:true,kCGImageSourceThumbnailMaxPixelSize:2000,kCGImageSourceCreateThumbnailWithTransform:true] as CFDictionary) else { return nil }
        return UIImage(cgImage:image)
    }
}
extension Color {
    init(hex:String) { let v = UInt64(hex.trimmingCharacters(in:CharacterSet.alphanumerics.inverted),radix:16) ?? 0x34594C; self.init(red:Double((v>>16)&255)/255,green:Double((v>>8)&255)/255,blue:Double(v&255)/255) }
    var rgbHex:String { let c = UIColor(self).resolvedColor(with:UITraitCollection(userInterfaceStyle:.dark)); var r:CGFloat=0,g:CGFloat=0,b:CGFloat=0,a:CGFloat=0; c.getRed(&r,green:&g,blue:&b,alpha:&a); return String(format:"%02X%02X%02X",Int(r*255),Int(g*255),Int(b*255)) }
}

@MainActor final class BackgroundArtwork: ObservableObject {
    static let shared = BackgroundArtwork()
    @Published var candidates:[String:[StoreArtwork]] = [:]
    @Published var images:[String:UIImage] = [:]
    private var attempted = Set<String>()
    private var loading = Set<String>()
    @Published private(set) var failedImages = Set<String>()
    func resolve(_ title:String) async {
        guard !title.isEmpty, !attempted.contains(title) else { return }; attempted.insert(title)
        #if DEBUG
        if ProcessInfo.processInfo.arguments.contains(where: { $0.hasPrefix("--ui-preview=") }) { candidates[title] = []; return }
        #endif
        let resolved = try? await StoreArtwork.lookup(title)
        if Task.isCancelled { attempted.remove(title); return }
        candidates[title] = resolved ?? []
        if candidates.count > 12 { candidates = candidates.filter { $0.key == title } }
    }
    func load(_ url:URL?) async {
        guard let url, images[url.absoluteString] == nil, !loading.contains(url.absoluteString), !failedImages.contains(url.absoluteString) else { return }
        let key=url.absoluteString; loading.insert(key); defer { loading.remove(key) }
        do {
            var request=URLRequest(url:url); request.timeoutInterval=15
            let (bytes,response)=try await URLSession.shared.data(for:request)
            guard (response as? HTTPURLResponse)?.statusCode == 200, bytes.count <= 16*1024*1024, let image=MobileAppearance.downsample(bytes) else { failedImages.insert(key); return }
            if images.count >= 4 { images.removeAll() }; images[key]=image
        } catch { if !Task.isCancelled { failedImages.insert(key) } }
    }
}

struct LabsBackdrop: View {
    @EnvironmentObject var m:MobileModel
    @EnvironmentObject var a:MobileAppearance
    @ObservedObject private var art=BackgroundArtwork.shared
    @State private var custom:UIImage?
    var body:some View {
        GeometryReader { geometry in
            let show = !a.oled && (a.background == "custom" || (a.background == "game" && (a.persistent || m.selectedTab == 1)))
            let selected = StoreArtwork.best((art.candidates[a.title] ?? []).filter { !art.failedImages.contains($0.url.absoluteString) },width:geometry.size.width,height:geometry.size.height) ?? StoreArtwork.secureURL(m.gameForTitle(a.title)?.image ?? "")
            let image = a.background == "custom" ? custom : selected.flatMap { art.images[$0.absoluteString] }
            ZStack {
                (a.oled ? Color.black : Color(red:0.035,green:0.075,blue:0.085))
                if show, let image {
                    Image(uiImage:image).resizable().aspectRatio(contentMode:a.fit ? .fit : .fill)
                        .frame(width:geometry.size.width,height:geometry.size.height).clipped().blur(radius:a.artBlur)
                    Color.black.opacity(a.dim)
                }
            }.frame(width:geometry.size.width,height:geometry.size.height).clipped()
            .task(id:a.title) { if a.background == "game", show { await art.resolve(a.title) } }
            .task(id:"\(a.background)-\(show)-\(selected?.absoluteString ?? "")") { if a.background == "game", show { await art.resolve(a.title); await art.load(selected) } }
            .task(id:a.customRevision) { custom = (try? Data(contentsOf:MobileAppearance.customURL)).flatMap(MobileAppearance.downsample) }
        }.ignoresSafeArea().allowsHitTesting(false).accessibilityHidden(true)
    }
}

struct AdjustableBlur:UIViewRepresentable {
    var intensity:Double
    final class Coordinator { var animator:UIViewPropertyAnimator?; var intensity:Double = -1; deinit { animator?.stopAnimation(true) } }
    func makeCoordinator()->Coordinator { Coordinator() }
    func makeUIView(context:Context)->UIVisualEffectView { UIVisualEffectView() }
    func updateUIView(_ view:UIVisualEffectView,context:Context) {
        guard abs(context.coordinator.intensity-intensity)>0.001 else { return }
        context.coordinator.animator?.stopAnimation(true); view.effect=nil
        let animator=UIViewPropertyAnimator(duration:1,curve:.linear) { view.effect=UIBlurEffect(style:.systemUltraThinMaterialDark) }
        animator.fractionComplete=max(0,min(1,intensity)); context.coordinator.animator=animator;context.coordinator.intensity=intensity
    }
    static func dismantleUIView(_ view:UIVisualEffectView,coordinator:Coordinator) { coordinator.animator?.stopAnimation(true) }
}
struct LabsGlassSurface:View {
    var highlight=false
    @EnvironmentObject var a:MobileAppearance
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    var body:some View {
        ZStack {
            if a.glass && !a.oled && !reduceTransparency { AdjustableBlur(intensity:a.blur) }
            (a.oled ? Color.black : Color(red:0.07,green:0.13,blue:0.15)).opacity(a.glass && !a.oled && !reduceTransparency ? 1-a.transparency : 1)
            if !a.oled { a.tintColor.opacity(0.14) }; if highlight { Color.green.opacity(0.1) }
        }.clipShape(RoundedRectangle(cornerRadius:17)).overlay { RoundedRectangle(cornerRadius:17).strokeBorder(.white.opacity(0.12),lineWidth:0.5) }
    }
}
struct LabsPageStyle:ViewModifier {
    func body(content:Content)->some View { content.scrollContentBackground(.hidden).background { LabsBackdrop() }.toolbarBackground(.hidden,for:.navigationBar) }
}
extension View {
    func labsPage()->some View { modifier(LabsPageStyle()) }
    func labsTile()->some View { padding(14).frame(maxWidth:.infinity,alignment:.leading).background { LabsGlassSurface() } }
}
struct AppearanceSettings:View {
    @EnvironmentObject var m:MobileModel
    @EnvironmentObject var a:MobileAppearance
    @State private var importing=false
    var body:some View {
        Section("Background") {
            Picker("Source",selection:$a.background) { Text("Current game artwork").tag("game");Text("Custom image").tag("custom");Text("Plain background").tag("none") }
            Toggle("Keep game art across the app",isOn:$a.persistent).disabled(a.background != "game")
            Button("Choose custom background…") { importing=true }
            Toggle("Fit full image",isOn:$a.fit)
            LabeledContent("Dimming",value:"\(Int(a.dim*100))%")
            Slider(value:$a.dim,in:0...0.85).accessibilityLabel("Background dimming")
            LabeledContent("Background blur",value:"\(Int(a.artBlur)) px")
            Slider(value:$a.artBlur,in:0...40).accessibilityLabel("Background blur")
            Text("Automatically prefers Microsoft Store poster art in tall windows and hero art in wide windows, including iPad Split View. Uses the closest available image when needed. Turn Fit off to fill the window.").font(.caption).foregroundStyle(.secondary)
        }.listRowBackground(LabsGlassSurface())
        Section("Liquid glass") {
            Toggle("Enable liquid glass",isOn:$a.glass)
            Toggle("Pure dark OLED mode",isOn:$a.oled)
            ColorPicker("Glass colour",selection:Binding(get:{a.tintColor},set:{a.tint=$0.rgbHex}),supportsOpacity:false).disabled(a.oled)
            ColorPicker("Accent colour",selection:Binding(get:{a.accentColor},set:{a.accent=$0.rgbHex}),supportsOpacity:false)
            LabeledContent("Glass transparency",value:"\(Int(a.transparency*100))%")
            Slider(value:$a.transparency,in:0...0.6).disabled(!a.glass || a.oled).accessibilityLabel("Glass transparency")
            LabeledContent("Blur strength",value:"\(Int(a.blur*100))%")
            Slider(value:$a.blur,in:0...1).disabled(!a.glass || a.oled).accessibilityLabel("Glass blur strength")
        }.listRowBackground(LabsGlassSurface())
        .fileImporter(isPresented:$importing,allowedContentTypes:[.image]) { result in
            do { try a.importBackground(result.get()) } catch { m.error=error.localizedDescription }
        }
    }
}
