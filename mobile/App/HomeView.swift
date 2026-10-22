import SwiftUI
import LabsMobileCore
struct HomeView: View {
    @EnvironmentObject var m: MobileModel
    @State private var sync = false
    @State private var embeddedLogin: SignInPage?
    @State private var safariPending = false
    @State private var redirect = ""
    @Environment(\.openURL) private var openURL
    private func shortcut(_ name:String,icon:String,tab:Int) -> some View {
        Button { m.selectedTab = tab } label: {
            VStack(spacing:8) { Image(systemName:icon).font(.title2); Text(name).font(.caption.weight(.semibold)) }.frame(maxWidth:.infinity).padding(.vertical,8)
        }
    }
    var body: some View {
        NavigationStack {
            List {
                Section {
                    HomeProfileHero()
                }.listRowBackground(Color.green.opacity(0.09))
                if m.account != nil {
                    Section("Jump back in") {
                        HStack(spacing:12) {
                            shortcut("Library",icon:"books.vertical",tab:0)
                            shortcut("Auto Unlock",icon:"list.bullet.rectangle",tab:1)
                            shortcut("Spoofing",icon:"gamecontroller",tab:2)
                        }.buttonStyle(.borderless)
                    }
                    if !m.games.isEmpty {
                        Section("Recently played") {
                            ForEach(Array(m.games.sorted { $0.lastPlayed > $1.lastPlayed }.prefix(3))) { game in
                                NavigationLink { AchievementView(game:game) } label: { LibraryGameRow(game:game) }
                            }
                        }
                    }
                }
                Section("Connect") {
                    Button { if let url = m.beginBrowserLogin() {
                        if m.signInBrowser == "Safari" { safariPending = true; openURL(url) }
                        else { embeddedLogin = SignInPage(url:url) }
                    } } label: { Label("Sign in with Microsoft",systemImage:"person.crop.circle.badge.checkmark") }.disabled(m.busy || m.running || m.presenceRunning)
                    Button { sync = true } label: { Label("Sync from PC",systemImage:"qrcode.viewfinder") }.disabled(m.busy || m.running || m.presenceRunning)
                    Text("Scan the codes from the mobile icon in the PC app, or use normal Microsoft sign-in.").font(.caption).foregroundStyle(.secondary)
                }
                if safariPending {
                    Section("Finish Safari sign-in") {
                        SecureField("Paste the final Microsoft redirect URL",text:$redirect).textContentType(.oneTimeCode).textInputAutocapitalization(.never).autocorrectionDisabled()
                        Button("Complete sign-in") { let value = redirect; redirect = ""; Task { await m.finishBrowserLogin(value); if m.account != nil { safariPending = false } } }.disabled(redirect.isEmpty || m.busy)
                    }
                }
                Section { Button("Settings and launch page",systemImage:"gear") { m.selectedTab = 3 } }
            }.listSectionSpacing(.compact).navigationTitle("Home").navigationBarTitleDisplayMode(.inline)
            .refreshable { await m.refreshGames() }
            .toolbar { Button { Task { await m.refreshGames() } } label: { Image(systemName:"arrow.clockwise") }.disabled(m.account == nil || m.busy).accessibilityLabel("Refresh profile and library") }
            .sheet(isPresented:$sync) { PCSyncView() }
            .sheet(item:$embeddedLogin) { page in
                EmbeddedSignIn(url:page.url,onComplete: { value in
                    embeddedLogin = nil; Task { await m.finishBrowserLogin(value) }
                },onCancel: { embeddedLogin = nil; m.cancelBrowserLogin() },remember:m.rememberSignIn).interactiveDismissDisabled()
            }
        }
    }
}

struct HomeProfileHero: View {
    @EnvironmentObject var m: MobileModel
    private let columns = [GridItem(.flexible()),GridItem(.flexible())]
    var body: some View {
        VStack(alignment:.leading,spacing:20) {
            HStack(spacing:16) {
                GameArt(url:m.account?.gamerpic ?? "",size:80).clipShape(Circle())
                VStack(alignment:.leading,spacing:5) {
                    Text(m.account == nil ? "Achievement Labs" : "Welcome back").font(.subheadline).foregroundStyle(.secondary)
                    Text(m.account?.gamertag ?? "Your Xbox homepage").font(.title2.bold()).textSelection(.enabled)
                    if let account = m.account {
                        Text((account.gamerscore?.isEmpty == false ? account.gamerscore! : "—") + " G").font(.title.bold()).foregroundStyle(.green).monospacedDigit()
                    }
                }
            }
            if m.account != nil {
                LazyVGrid(columns:columns,alignment:.leading,spacing:12) {
                    metric("Games",value:m.games.count.formatted(),icon:"books.vertical")
                    metric("Achievements",value:m.libraryAchievements.formatted(),icon:"trophy")
                    metric("Mythic · known",value:m.mythicCount.formatted(),icon:"star.circle")
                    metric("100% complete",value:m.games.filter(\.complete).count.formatted(),icon:"checkmark.seal")
                }
                Text("Library totals · Mythic count reflects known completions").font(.caption2).foregroundStyle(.secondary)
            } else { Text("Sign in with Microsoft or scan your PC’s private sync codes to get started.").font(.subheadline).foregroundStyle(.secondary) }
        }.padding(.vertical,12)
    }
    private func metric(_ title:String,value:String,icon:String) -> some View {
        VStack(alignment:.leading,spacing:8) {
            Label(title,systemImage:icon).font(.caption).foregroundStyle(.secondary)
            Text(value).font(.title.bold()).monospacedDigit().minimumScaleFactor(0.7).lineLimit(1)
        }.frame(maxWidth:.infinity,alignment:.leading).padding(14).background(Color.green.opacity(0.07),in:RoundedRectangle(cornerRadius:14))
    }
}
