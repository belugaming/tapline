import SwiftUI
import TaplineCore

extension SpeakerPosition {
    var name: String {
        switch self {
        case .frontLeft: String(localized: "Front left")
        case .frontRight: String(localized: "Front right")
        case .center: String(localized: "Front center")
        case .lfe: String(localized: "Subwoofer (LFE)")
        case .lfe2: String(localized: "Second subwoofer (LFE 2)")
        case .frontLeftCenter: String(localized: "Front left center")
        case .frontRightCenter: String(localized: "Front right center")
        case .wideLeft: String(localized: "Front wide left")
        case .wideRight: String(localized: "Front wide right")
        case .surroundLeft: String(localized: "Surround left")
        case .surroundRight: String(localized: "Surround right")
        case .rearLeft: String(localized: "Rear surround left")
        case .rearRight: String(localized: "Rear surround right")
        case .rearCenter: String(localized: "Rear center")
        case .topFrontLeft: String(localized: "Top front left")
        case .topFrontCenter: String(localized: "Top front center")
        case .topFrontRight: String(localized: "Top front right")
        case .topMiddleLeft: String(localized: "Top middle left")
        case .topMiddleCenter: String(localized: "Top middle center")
        case .topMiddleRight: String(localized: "Top middle right")
        case .topRearLeft: String(localized: "Top rear left")
        case .topRearCenter: String(localized: "Top rear center")
        case .topRearRight: String(localized: "Top rear right")
        case .unknown: String(localized: "Unknown")
        }
    }
}

/// The speakers of a layout seen from above, the listener in the middle and the screen at the
/// top: ear-level speakers on the outer ring, height speakers on the inner one. A speaker can
/// be highlighted (playing now), outlined (asked about) and marked as checked.
struct SpeakerMap: View {
    let layout: SpeakerLayout
    var playing: Int?
    var asking: Int?
    /// Wiring check answers: true where the speaker played from its place.
    var marks: [Int: Bool] = [:]
    var onTap: ((Int) -> Void)?

    var body: some View {
        GeometryReader { geo in
            let size = geo.size
            ZStack {
                Canvas { ctx, size in
                    let c = center(size)
                    let (rx, ry) = radii(size)
                    // The screen, the ear-level ring and the height ring.
                    let screen = CGRect(x: c.x - rx * 0.45, y: 5, width: rx * 0.9, height: 3)
                    ctx.fill(Path(roundedRect: screen, cornerRadius: 1.5), with: .color(.white.opacity(0.28)))
                    ctx.stroke(Path(ellipseIn: CGRect(x: c.x - rx, y: c.y - ry, width: 2 * rx, height: 2 * ry)),
                               with: .color(.white.opacity(0.12)), style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
                    if layout.positions.contains(where: \.isHeight) {
                        let h = Self.heightRing
                        ctx.stroke(Path(ellipseIn: CGRect(x: c.x - rx * h, y: c.y - ry * h, width: 2 * rx * h, height: 2 * ry * h)),
                                   with: .color(.white.opacity(0.08)), style: StrokeStyle(lineWidth: 1, dash: [2, 3]))
                    }
                    // The listener.
                    ctx.stroke(Path(ellipseIn: CGRect(x: c.x - 9, y: c.y - 9, width: 18, height: 18)), with: .color(.white.opacity(0.3)), lineWidth: 1)
                    ctx.fill(Path(ellipseIn: CGRect(x: c.x - 2, y: c.y - 11, width: 4, height: 4)), with: .color(.white.opacity(0.3)))
                }
                ForEach(layout.speakerChannels, id: \.self) { channel in
                    chip(channel)
                        .position(point(channel, in: size))
                }
            }
        }
        .instrumentPanel(radius: 8)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(Text("Speaker layout \(layout.name)"))
    }

    static let heightRing = 0.52

    private func center(_ size: CGSize) -> CGPoint { CGPoint(x: size.width / 2, y: size.height * 0.53) }
    private func radii(_ size: CGSize) -> (CGFloat, CGFloat) { (size.width * 0.40, size.height * 0.38) }

    private func point(_ channel: Int, in size: CGSize) -> CGPoint {
        let p = layout.positions[channel]
        // Subwoofers sit in the front corners of the room.
        if p.isLFE { return CGPoint(x: p == .lfe ? 26 : size.width - 26, y: 24) }
        let c = center(size)
        let (rx, ry) = radii(size)
        let scale = p.isHeight ? Self.heightRing : 1
        let a = p.azimuth * .pi / 180
        return CGPoint(x: c.x + rx * scale * sin(a), y: c.y - ry * scale * cos(a))
    }

    @ViewBuilder
    private func chip(_ channel: Int) -> some View {
        let p = layout.positions[channel]
        let isPlaying = playing == channel
        let mark = marks[channel]
        let label = Text(p.abbreviation)
            .font(.system(size: 9, weight: .semibold).monospacedDigit())
            .foregroundStyle(isPlaying ? Color.black : .white.opacity(0.85))
            .padding(.horizontal, 4)
            .frame(minWidth: 26, minHeight: 16)
            .background(isPlaying ? Theme.accent : Color.white.opacity(p.isHeight ? 0.10 : 0.16),
                        in: RoundedRectangle(cornerRadius: p.isHeight ? 8 : 3, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: p.isHeight ? 8 : 3, style: .continuous)
                .strokeBorder(asking == channel ? Theme.accent : .clear, lineWidth: 1.5))
            .overlay(alignment: .topTrailing) {
                if let mark {
                    Image(systemName: mark ? "checkmark.circle.fill" : "xmark.circle.fill")
                        .font(.system(size: 8))
                        .foregroundStyle(mark ? Theme.accent : Theme.danger)
                        .background(Circle().fill(Theme.panel))
                        .offset(x: 4, y: -4)
                }
            }
            .help(Text(verbatim: "\(p.name) · \(String(localized: "channel \(channel + 1)"))"))
        if let onTap {
            label.onTapGesture { onTap(channel) }
        } else {
            label
        }
    }
}
