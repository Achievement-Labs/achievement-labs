import SwiftUI
import LabsMobileCore

struct GameArt: View {
    let url: String
    var size: CGFloat = 46
    var body: some View {
        AsyncImage(url:URL(string:url)) { phase in
            if let image = phase.image { image.resizable().scaledToFill() }
            else { ZStack { Color.white.opacity(0.06); Image(systemName:"gamecontroller").foregroundStyle(.secondary) } }
        }.frame(width:size,height:size).clipShape(RoundedRectangle(cornerRadius:8))
    }
}
/// Same star-and-laurel geometry used by the desktop fork.
struct MythicShape: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        p.move(to:CGPoint(x:50,y:12))
        for v in [(59,34),(83,36),(65,52),(71,77),(50,63),(29,77),(35,52),(17,36),(41,34)] { p.addLine(to:CGPoint(x:CGFloat(v.0),y:CGFloat(v.1))) }; p.closeSubpath()
        func leaf(_ start: CGPoint, _ c1: CGPoint, _ e1: CGPoint, _ c2: CGPoint, _ e2: CGPoint, _ c3: CGPoint, _ e3: CGPoint) {
            p.move(to:start); p.addQuadCurve(to:e1,control:c1); p.addQuadCurve(to:e2,control:c2); p.addQuadCurve(to:e3,control:c3); p.closeSubpath()
        }
        leaf(CGPoint(x:20,y:70),CGPoint(x:5,y:49),CGPoint(x:16,y:25),CGPoint(x:2,y:31),CGPoint(x:3,y:45),CGPoint(x:4,y:57),CGPoint(x:14,y:65))
        p.move(to:CGPoint(x:17,y:77)); p.addQuadCurve(to:CGPoint(x:1,y:62),control:CGPoint(x:8,y:62)); p.addQuadCurve(to:CGPoint(x:19,y:85),control:CGPoint(x:0,y:78)); p.closeSubpath()
        p.move(to:CGPoint(x:27,y:87)); p.addQuadCurve(to:CGPoint(x:9,y:81),control:CGPoint(x:16,y:77)); p.addQuadCurve(to:CGPoint(x:34,y:96),control:CGPoint(x:14,y:95)); p.closeSubpath()
        leaf(CGPoint(x:80,y:70),CGPoint(x:95,y:49),CGPoint(x:84,y:25),CGPoint(x:98,y:31),CGPoint(x:97,y:45),CGPoint(x:96,y:57),CGPoint(x:86,y:65))
        p.move(to:CGPoint(x:83,y:77)); p.addQuadCurve(to:CGPoint(x:99,y:62),control:CGPoint(x:92,y:62)); p.addQuadCurve(to:CGPoint(x:81,y:85),control:CGPoint(x:100,y:78)); p.closeSubpath()
        p.move(to:CGPoint(x:73,y:87)); p.addQuadCurve(to:CGPoint(x:91,y:81),control:CGPoint(x:84,y:77)); p.addQuadCurve(to:CGPoint(x:66,y:96),control:CGPoint(x:86,y:95)); p.closeSubpath()
        return p.applying(CGAffineTransform(scaleX:rect.width/100,y:rect.height/100).concatenating(CGAffineTransform(translationX:rect.minX,y:rect.minY)))
    }
}
struct MythicMark: View {
    var body: some View { MythicShape().fill(Color(red:112/255,green:201/255,blue:138/255)).frame(width:20,height:20).accessibilityLabel("Mythic: base game complete") }
}
struct GameHeader: View {
    let game: Game
    @EnvironmentObject var m: MobileModel
    var body: some View {
        HStack(spacing:12) {
            GameArt(url:game.image,size:56)
            VStack(alignment:.leading,spacing:4) {
                Text(game.name).font(.headline).textSelection(.enabled)
                Text(game.platforms.joined(separator:" / ")).font(.caption2).foregroundStyle(.secondary)
                HStack(spacing:6) {
                    Text("\(game.unlocked)/\(game.total > 0 ? String(game.total) : "?") achievements").font(.caption)
                    if m.showMythic && m.hasMythic(game) { MythicMark() }
                }
                Text("Title ID \(game.id)").font(.caption2).foregroundStyle(.secondary).textSelection(.enabled)
            }
        }.padding(.vertical,2)
    }
}
struct LibraryGameRow: View {
    let game: Game
    @EnvironmentObject var m: MobileModel
    var body: some View {
        HStack(spacing:10) {
            GameArt(url:game.image)
            VStack(alignment:.leading,spacing:4) {
                Text(game.name).font(.subheadline.weight(.semibold)).lineLimit(2)
                Text(game.platforms.joined(separator:" / ")).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                HStack(spacing:5) {
                    Text("\(game.unlocked)/\(game.total > 0 ? String(game.total) : "?")").font(.caption)
                    if m.showMythic && m.hasMythic(game) { MythicMark().scaleEffect(0.85).frame(width:18,height:16) }
                    Spacer(minLength:4)
                    Text(game.id).font(.caption2).foregroundStyle(.tertiary)
                }
            }
        }.padding(.vertical,2)
    }
}
struct ProfileTile: View {
    @EnvironmentObject var m: MobileModel
    var body: some View {
        VStack(alignment:.leading,spacing:10) {
            HStack(spacing:10) {
                GameArt(url:m.account?.gamerpic ?? "",size:42).clipShape(Circle())
                VStack(alignment:.leading,spacing:3) {
                    Text(m.account?.gamertag ?? "Connect your Xbox account").font(.headline).textSelection(.enabled)
                    Text(m.account.map { ($0.gamerscore?.isEmpty == false ? $0.gamerscore! : "—") + " G" } ?? "Open Settings to sign in").font(.subheadline).foregroundStyle(.green)
                }
            }
            if m.account != nil {
                HStack {
                    stat("Games",value:m.games.count.formatted())
                    stat("Achievements",value:m.libraryAchievements.formatted())
                    stat("Mythic · known",value:m.mythicCount.formatted())
                }
            }
        }.padding(.vertical,3)
    }
    private func stat(_ label: String, value: String) -> some View {
        VStack(alignment:.leading,spacing:2) { Text(value).font(.subheadline.weight(.semibold)).monospacedDigit(); Text(label).font(.caption2).foregroundStyle(.secondary) }.frame(maxWidth:.infinity,alignment:.leading)
    }
}
struct SpoofSummary: View {
    @EnvironmentObject var m: MobileModel
    var body: some View {
        HStack(spacing:10) {
            if let game = m.gameForTitle(m.presenceTitle) { GameArt(url:game.image,size:32) }
            VStack(alignment:.leading,spacing:3) {
                Text(m.gameForTitle(m.presenceTitle)?.name ?? "Title \(m.presenceTitle)").font(.caption.weight(.semibold)).lineLimit(1)
                if let start = m.presenceStarted { Text(start,style:.timer).font(.caption.monospacedDigit()).foregroundStyle(.green) }
                else { Text(m.presenceRunning ? "Connecting…" : "Stopped").font(.caption2).foregroundStyle(.secondary) }
            }
            Spacer()
            Button("Open") { m.selectedTab = 1 }.font(.caption)
        }.activeSessionHighlight(m.presenceRunning)
    }
}

