import SwiftUI
import LabsMobileCore

struct FindGamesView: View {
    @EnvironmentObject var m: MobileModel
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var results: [Game] = []
    @State private var message = "Search by game name or enter a decimal Xbox Title ID. Games do not need to be on your profile."
    @State private var searching = false
    @State private var opening = false
    @State private var request = 0
    @State private var selected: Game?
    @State private var platforms = Set(TitlePlatform.allCases)
    @State private var directLookup = false
    private var visible: [Game] { results.filter { directLookup || !TitlePlatform.groups($0.platforms).isDisjoint(with:platforms) } }
    private var offlinePreview: Bool {
        #if DEBUG
        return ProcessInfo.processInfo.arguments.contains { $0.hasPrefix("--ui-preview=") }
        #else
        return false
        #endif
    }
    var body: some View {
        NavigationStack {
            List {
                Section {
                    TextField("Game name or Title ID",text:$query).textInputAutocapitalization(.never).autocorrectionDisabled().submitLabel(.search).onSubmit { if !searching { request += 1 } }
                    Button("Search all games",systemImage:"magnifyingglass") { request += 1 }.disabled(query.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty || searching || opening)
                    if searching || opening { ProgressView() }
                    Text(message).font(.caption).foregroundStyle(.secondary)
                }
                Section {
                    HStack {
                        Text("Platforms").font(.headline)
                        Spacer()
                        Button("All") { platforms = Set(TitlePlatform.allCases) }
                        Button("None") { platforms = [] }
                    }.buttonStyle(.borderless)
                    LazyVGrid(columns:[GridItem(.adaptive(minimum:125))],alignment:.leading,spacing:8) {
                        ForEach(TitlePlatform.allCases) { platform in
                            Toggle(platform.label,isOn:Binding(get:{ platforms.contains(platform) },set:{ if $0 { platforms.insert(platform) } else { platforms.remove(platform) } }))
                                .toggleStyle(.button).font(.caption)
                        }
                    }
                    Text("Filters apply to name searches. A Title ID lookup shows the exact title, even before its platform is known.").font(.caption).foregroundStyle(.secondary)
                }
                Section("Results · \(visible.count) / \(results.count)") {
                    if !results.isEmpty && visible.isEmpty { Text(platforms.isEmpty ? "Select at least one platform." : "No matches on the selected platforms. Try All.").foregroundStyle(.secondary) }
                    ForEach(visible) { game in
                        Button {
                            opening = true
                            Task {
                                defer { opening = false }
                                do { selected = try await m.resolveFoundGame(game) }
                                catch { message = error.localizedDescription }
                            }
                        } label: {
                            VStack(alignment:.leading,spacing:4) {
                                Text(game.name).foregroundStyle(.primary)
                                Text("Title ID \(game.id) · \(game.platforms.isEmpty ? "Platform unavailable" : game.platforms.joined(separator:" / "))").font(.caption).foregroundStyle(.secondary)
                            }
                        }.disabled(opening || m.busy || m.account == nil)
                    }
                }
                Section {
                    Text("Choose the correct edition to open its achievements. Xbox sign-in is required to view account progress. Search results do not confirm achievement or event support and do not add games to your profile.").font(.caption).foregroundStyle(.secondary)
                }
            }.navigationTitle("Find any game").navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement:.cancellationAction) { Button("Done") { dismiss() } } }
            .navigationDestination(item:$selected) { AchievementView(game:$0) }
            .task(id:request) {
                guard request > 0 else { return }
                searching = true; results = []; message = "Searching the public game catalogue…"
                directLookup = (try? TitleSearch.direct(query)) != nil
                defer { searching = false }
                do {
                    let found: [Game]
                    if offlinePreview {
                        found = try TitleSearch.parse(Data(#"{"items":[{"title_id":"00000009","name":"Example game outside your profile","systems":["XBOXONE"]},{"title_id":"0000000A","name":"Example PC edition","systems":["PC"]}]}"#.utf8))
                    } else {
                        let response = try await PublicSync().searchTitleResults(query)
                        try Task.checkCancellation(); results = response.games
                        message = "\(results.count) results (up to 300 across spellings)." + (response.partial ? " Some spellings were unavailable; results may be incomplete." : "")
                        if results.isEmpty { message = response.partial ? "Search was incomplete. Retry or enter a Title ID." : "No games found. Try another name or enter a Title ID." }
                        return
                    }
                    try Task.checkCancellation(); results = found
                    message = found.isEmpty ? "No games found. Try another name or enter a Title ID." : "\(found.count) results. Choose the correct platform and edition."
                } catch is CancellationError { }
                catch { message = "Search unavailable: \(error.localizedDescription) You can also enter a Title ID." }
            }
            .onAppear {
                if offlinePreview && request == 0 { query = "Example"; request = 1 }
            }
        }
    }
}
