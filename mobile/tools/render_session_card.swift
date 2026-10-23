import AppKit
import Foundation

// Production SwiftUI card; only the native ticking timer text is held still for screenshots.
@MainActor func renderCard(_ name: String,width: CGFloat,spoof: Bool = true,queue: Bool = true,stale: Bool = false,longNames: Bool = false) throws {
    let view = SessionActivityCard(
        spoofActive:spoof,queueActive:queue,stale:stale,
        spoofGame:longNames ? "A very long game name: Definitive Edition" : "Palworld",
        queueGame:longNames ? "Another very long game: Complete Edition" : "Palworld",
        nextAchievement:longNames ? "An achievement with a long name that must never cover the countdown" : "Legendary Ocean King",
        handled:0,total:16,
        spoofTimer: { Text(longNames ? "12:34:56" : "0:51") },
        unlockTimer: { Text(longNames ? "23:59:59" : "42:41") }
    ).frame(width:width).background(.black).environment(\.colorScheme,.dark)
    let renderer = ImageRenderer(content:view); renderer.scale = 2
    guard let cg = renderer.cgImage else { throw NSError(domain:"Layout",code:1) }
    guard !spoof || !queue || cg.height <= 320 else { throw NSError(domain:"Layout exceeds compact activity height",code:2,userInfo:["height":cg.height]) }
    let bitmap = NSBitmapImageRep(cgImage:cg)
    guard let png = bitmap.representation(using:.png,properties:[:]) else { throw NSError(domain:"Layout",code:3) }
    try png.write(to:URL(fileURLWithPath:"mobile/layout-previews/" + name + ".png"))
    print("Rendered \(name): \(cg.width/2) × \(cg.height/2) points")
}
try MainActor.assumeIsolated {
    try renderCard("iPhone-small",width:300)
    try renderCard("iPhone-regular",width:360)
    try renderCard("iPhone-long-names",width:300,longNames:true)
    try renderCard("iPhone-spoof-only",width:300,queue:false)
    try renderCard("iPhone-unlock-only",width:300,spoof:false)
    try renderCard("iPhone-stale",width:300,stale:true)
    try renderCard("iPad",width:600)
}
