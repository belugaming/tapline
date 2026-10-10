import SwiftUI
import TaplineCore

/// Vertical stereo peak/RMS meter with peak-hold ticks and a clip LED.
struct StereoMeter: View {
    @Environment(AppModel.self) private var model
    var height: CGFloat? = nil

    var body: some View {
        let m = model.meters
        VStack(spacing: 6) {
            Circle()
                .fill(m.clipping ? Theme.danger : Color.white.opacity(0.12))
                .frame(width: 7, height: 7)
                .help(Text("Clipping: lower the preamp or enable the limiter"))
            HStack(spacing: 3) {
                MeterBar(peak: m.peakL, rms: m.rmsL, hold: m.holdL)
                MeterBar(peak: m.peakR, rms: m.rmsR, hold: m.holdR)
            }
            .frame(width: 18)
            .frame(maxHeight: height ?? .infinity)
            Text(Format.meterDB(max(m.holdL, m.holdR)))
                .font(.system(size: 9, weight: .medium).monospacedDigit())
                .foregroundStyle(Theme.gridLabel)
                .frame(width: 34)
        }
    }
}

struct MeterBar: View {
    let peak: Float
    let rms: Float
    let hold: Float
    var horizontal = false

    static let floorDB = -60.0

    static func position(_ linear: Float) -> CGFloat {
        let db = linearToDB(Double(linear))
        return CGFloat(((db - floorDB) / -floorDB).clamped(to: 0...1))
    }

    var body: some View {
        GeometryReader { geo in
            let length = horizontal ? geo.size.width : geo.size.height
            let p = Self.position(peak) * length
            let r = Self.position(rms) * length
            let h = Self.position(hold) * length
            ZStack(alignment: horizontal ? .leading : .bottom) {
                RoundedRectangle(cornerRadius: 2).fill(Color.white.opacity(0.07))
                gradient
                    .mask(alignment: horizontal ? .leading : .bottom) {
                        Rectangle().frame(width: horizontal ? p : nil, height: horizontal ? nil : p)
                    }
                    .opacity(0.55)
                gradient
                    .mask(alignment: horizontal ? .leading : .bottom) {
                        Rectangle().frame(width: horizontal ? r : nil, height: horizontal ? nil : r)
                    }
                if hold > 0.0005 {
                    Rectangle()
                        .fill(Color.white.opacity(0.85))
                        .frame(width: horizontal ? 1.5 : nil, height: horizontal ? nil : 1.5)
                        .offset(x: horizontal ? h - 1 : 0, y: horizontal ? 0 : -(h - 1))
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 2))
        }
    }

    private var gradient: LinearGradient {
        LinearGradient(
            stops: [
                .init(color: Theme.accent, location: 0),
                .init(color: Theme.accent, location: 0.75),
                .init(color: Theme.warning, location: 0.9),
                .init(color: Theme.danger, location: 1),
            ],
            startPoint: horizontal ? .leading : .bottom,
            endPoint: horizontal ? .trailing : .top
        )
    }
}

/// Thin horizontal meter used in the mixer rows.
struct MiniMeter: View {
    let level: Float

    var body: some View {
        MeterBar(peak: level, rms: level * 0.7, hold: 0, horizontal: true)
            .frame(height: 4)
    }
}
