import SwiftUI
import TaplineCore

/// Small, non-interactive frequency response preview.
struct CurveThumbnail: View {
    let bands: [EQBand]
    var tone = ToneSettings()
    var range: Double = 12
    var color: Color = Theme.accent
    var showGrid = true

    private static let frequencies = ResponseCurve.logFrequencies(count: 120)

    var body: some View {
        Canvas { ctx, size in
            let response = ResponseCurve.responseDB(bands: bands.filter(\.isEnabled) + tone.bands.filter(\.isEnabled), frequencies: Self.frequencies, sampleRate: 48_000)
            let midY = size.height / 2
            if showGrid {
                var zero = Path()
                zero.move(to: CGPoint(x: 0, y: midY))
                zero.addLine(to: CGPoint(x: size.width, y: midY))
                ctx.stroke(zero, with: .color(.white.opacity(0.15)), lineWidth: 0.5)
            }
            var path = Path()
            for (i, db) in response.enumerated() {
                let x = size.width * CGFloat(i) / CGFloat(response.count - 1)
                let y = midY - CGFloat(db.clamped(to: -range...range) / range) * (size.height / 2 - 2)
                if i == 0 { path.move(to: CGPoint(x: x, y: y)) } else { path.addLine(to: CGPoint(x: x, y: y)) }
            }
            var fill = path
            fill.addLine(to: CGPoint(x: size.width, y: midY))
            fill.addLine(to: CGPoint(x: 0, y: midY))
            fill.closeSubpath()
            ctx.fill(fill, with: .color(color.opacity(0.18)))
            ctx.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: 1.5, lineJoin: .round))
        }
    }
}
