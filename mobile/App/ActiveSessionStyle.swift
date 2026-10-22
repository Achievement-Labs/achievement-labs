import SwiftUI

/// Static highlighting: no pulsing animation or additional timer wakeups.
struct ActiveSessionStyle: ViewModifier {
    var active: Bool
    var radius: CGFloat = 12
    var inset: CGFloat = 12
    func body(content: Content) -> some View {
        content.padding(inset)
            .background {
                RoundedRectangle(cornerRadius:radius)
                    .fill(active ? Color.green.opacity(0.055) : Color.clear)
                    .overlay { RoundedRectangle(cornerRadius:radius).strokeBorder(active ? Color.green.opacity(0.72) : Color.clear,lineWidth:1) }
                    .shadow(color:active ? Color.green.opacity(0.24) : .clear,radius:5)
            }
    }
}
extension View {
    func activeSessionHighlight(_ active: Bool, radius: CGFloat = 12, inset: CGFloat = 12) -> some View {
        modifier(ActiveSessionStyle(active:active,radius:radius,inset:inset))
    }
}
