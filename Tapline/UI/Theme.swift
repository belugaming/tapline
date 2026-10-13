import SwiftUI
import TaplineCore

enum Theme {
    /// The instrument panel behind graphs is always dark, like hardware.
    static let panel = Color(red: 0.055, green: 0.063, blue: 0.078)
    static let panelRaised = Color(red: 0.086, green: 0.098, blue: 0.118)
    static let panelStroke = Color.white.opacity(0.07)
    static let gridMajor = Color.white.opacity(0.10)
    static let gridMinor = Color.white.opacity(0.045)
    static let gridLabel = Color.white.opacity(0.42)
    static let curve = Color.white
    /// The right channel's curve when left and right differ.
    static let curveRight = Color(red: 0.55, green: 0.76, blue: 1.0)
    static let spectrumPost = Color(red: 0.25, green: 0.78, blue: 0.95)
    static let spectrumPre = Color.white.opacity(0.22)
    static let accent = Color(red: 0.18, green: 0.80, blue: 0.62)
    static let warning = Color(red: 1.0, green: 0.72, blue: 0.28)
    static let danger = Color(red: 1.0, green: 0.36, blue: 0.36)

    /// Distinct hues of similar lightness so no band looks more important than another.
    static let bandPalette: [Color] = [
        Color(red: 1.00, green: 0.45, blue: 0.42),
        Color(red: 1.00, green: 0.71, blue: 0.30),
        Color(red: 0.70, green: 0.88, blue: 0.36),
        Color(red: 0.25, green: 0.84, blue: 0.70),
        Color(red: 0.33, green: 0.73, blue: 1.00),
        Color(red: 0.53, green: 0.58, blue: 1.00),
        Color(red: 0.79, green: 0.51, blue: 1.00),
        Color(red: 1.00, green: 0.50, blue: 0.78),
    ]

    static func bandColor(_ index: Int) -> Color { bandPalette[index % bandPalette.count] }
}

enum Format {
    static func frequency(_ hz: Double, unit: Bool = true) -> String {
        let s: String
        if hz >= 10_000 { s = String(format: "%.1fk", hz / 1000) }
        else if hz >= 1000 { s = String(format: "%.2fk", hz / 1000) }
        else if hz >= 100 { s = String(format: "%.0f", hz) }
        else { s = String(format: "%.1f", hz) }
        return unit ? s + " Hz" : s
    }

    static func gridFrequency(_ hz: Double) -> String {
        hz >= 1000 ? String(format: "%gk", hz / 1000) : String(format: "%g", hz)
    }

    static func gain(_ db: Double) -> String {
        let v = abs(db) < 0.05 ? 0 : db
        return String(format: "%+.1f dB", v)
    }

    static func q(_ q: Double) -> String { String(format: "%.2f", q) }

    static func percent(_ v: Double) -> String { String(format: "%.0f%%", v * 100) }

    static func sampleRate(_ hz: Double) -> String { String(format: "%g kHz", hz / 1000) }

    static func meterDB(_ linear: Float) -> String {
        let db = linearToDB(Double(linear))
        return db < -90 ? "-∞" : String(format: "%.1f", db)
    }
}

extension FilterType {
    var displayName: LocalizedStringKey {
        switch self {
        case .peak: "Bell"
        case .lowShelf: "Low Shelf"
        case .highShelf: "High Shelf"
        case .lowPass: "Low Pass"
        case .highPass: "High Pass"
        case .notch: "Notch"
        case .bandPass: "Band Pass"
        }
    }

    var symbol: String {
        switch self {
        case .peak: "waveform.path"
        case .lowShelf: "chart.line.downtrend.xyaxis"
        case .highShelf: "chart.line.uptrend.xyaxis"
        case .lowPass: "line.diagonal.arrow"
        case .highPass: "arrow.up.right"
        case .notch: "chevron.down"
        case .bandPass: "chevron.up"
        }
    }

    /// Small hand-drawn glyph of the filter shape, used in pickers and chips.
    func glyph(in rect: CGRect) -> Path {
        var p = Path()
        let w = rect.width, h = rect.height, x = rect.minX, y = rect.minY
        let mid = y + h * 0.55
        switch self {
        case .peak:
            p.move(to: CGPoint(x: x, y: mid))
            p.addCurve(to: CGPoint(x: x + w * 0.5, y: y + h * 0.1), control1: CGPoint(x: x + w * 0.3, y: mid), control2: CGPoint(x: x + w * 0.38, y: y + h * 0.1))
            p.addCurve(to: CGPoint(x: x + w, y: mid), control1: CGPoint(x: x + w * 0.62, y: y + h * 0.1), control2: CGPoint(x: x + w * 0.7, y: mid))
        case .lowShelf:
            p.move(to: CGPoint(x: x, y: y + h * 0.2))
            p.addLine(to: CGPoint(x: x + w * 0.3, y: y + h * 0.2))
            p.addCurve(to: CGPoint(x: x + w * 0.7, y: mid), control1: CGPoint(x: x + w * 0.5, y: y + h * 0.2), control2: CGPoint(x: x + w * 0.5, y: mid))
            p.addLine(to: CGPoint(x: x + w, y: mid))
        case .highShelf:
            p.move(to: CGPoint(x: x, y: mid))
            p.addLine(to: CGPoint(x: x + w * 0.3, y: mid))
            p.addCurve(to: CGPoint(x: x + w * 0.7, y: y + h * 0.2), control1: CGPoint(x: x + w * 0.5, y: mid), control2: CGPoint(x: x + w * 0.5, y: y + h * 0.2))
            p.addLine(to: CGPoint(x: x + w, y: y + h * 0.2))
        case .lowPass:
            p.move(to: CGPoint(x: x, y: y + h * 0.35))
            p.addLine(to: CGPoint(x: x + w * 0.45, y: y + h * 0.35))
            p.addCurve(to: CGPoint(x: x + w * 0.9, y: y + h), control1: CGPoint(x: x + w * 0.65, y: y + h * 0.3), control2: CGPoint(x: x + w * 0.75, y: y + h * 0.8))
        case .highPass:
            p.move(to: CGPoint(x: x + w * 0.1, y: y + h))
            p.addCurve(to: CGPoint(x: x + w * 0.55, y: y + h * 0.35), control1: CGPoint(x: x + w * 0.25, y: y + h * 0.8), control2: CGPoint(x: x + w * 0.35, y: y + h * 0.3))
            p.addLine(to: CGPoint(x: x + w, y: y + h * 0.35))
        case .notch:
            p.move(to: CGPoint(x: x, y: y + h * 0.3))
            p.addLine(to: CGPoint(x: x + w * 0.4, y: y + h * 0.3))
            p.addLine(to: CGPoint(x: x + w * 0.5, y: y + h))
            p.addLine(to: CGPoint(x: x + w * 0.6, y: y + h * 0.3))
            p.addLine(to: CGPoint(x: x + w, y: y + h * 0.3))
        case .bandPass:
            p.move(to: CGPoint(x: x, y: y + h))
            p.addCurve(to: CGPoint(x: x + w * 0.5, y: y + h * 0.2), control1: CGPoint(x: x + w * 0.3, y: y + h), control2: CGPoint(x: x + w * 0.35, y: y + h * 0.2))
            p.addCurve(to: CGPoint(x: x + w, y: y + h), control1: CGPoint(x: x + w * 0.65, y: y + h * 0.2), control2: CGPoint(x: x + w * 0.7, y: y + h))
        }
        return p
    }
}

struct FilterGlyph: View {
    let type: FilterType
    var lineWidth: CGFloat = 1.6

    var body: some View {
        Canvas { ctx, size in
            ctx.stroke(type.glyph(in: CGRect(origin: .zero, size: size).insetBy(dx: 1, dy: 2)), with: .foreground, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round, lineJoin: .round))
        }
    }
}

/// A rounded dark "instrument" container.
struct PanelBackground: ViewModifier {
    var radius: CGFloat = 12

    func body(content: Content) -> some View {
        content
            .background(Theme.panel, in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).strokeBorder(Theme.panelStroke))
    }
}

extension View {
    func instrumentPanel(radius: CGFloat = 12) -> some View { modifier(PanelBackground(radius: radius)) }
}

/// A titled card used on the settings-style pages.
struct Card<Content: View>: View {
    let title: LocalizedStringKey
    var subtitle: LocalizedStringKey?
    var systemImage: String?
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 8) {
                if let systemImage {
                    Image(systemName: systemImage)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(.secondary)
                        .frame(width: 18)
                }
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.headline)
                    if let subtitle {
                        Text(subtitle).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            content
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.separator.opacity(0.6)))
    }
}
