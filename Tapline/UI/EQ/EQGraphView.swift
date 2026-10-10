import AppKit
import SwiftUI
import TaplineCore

/// Maps between graph coordinates and frequency / gain / spectrum level.
struct GraphMapper {
    let size: CGSize
    let dbRange: Double
    var insets = EdgeInsets(top: 12, leading: 36, bottom: 22, trailing: 12)

    static let minFrequency = 20.0
    static let maxFrequency = 20_000.0
    static let spectrumFloor = -90.0
    static let spectrumCeiling = 6.0

    var plot: CGRect {
        CGRect(x: insets.leading, y: insets.top,
               width: max(1, size.width - insets.leading - insets.trailing),
               height: max(1, size.height - insets.top - insets.bottom))
    }

    func x(_ f: Double) -> CGFloat {
        let n = log10(f / Self.minFrequency) / log10(Self.maxFrequency / Self.minFrequency)
        return plot.minX + CGFloat(n) * plot.width
    }

    func frequency(atX x: CGFloat) -> Double {
        let n = Double((x - plot.minX) / plot.width).clamped(to: 0...1)
        return Self.minFrequency * pow(Self.maxFrequency / Self.minFrequency, n)
    }

    func y(_ db: Double) -> CGFloat {
        plot.midY - CGFloat(db / dbRange) * plot.height / 2
    }

    func db(atY y: CGFloat) -> Double {
        Double((plot.midY - y) / (plot.height / 2)) * dbRange
    }

    func spectrumY(_ level: Float) -> CGFloat {
        let n = (Double(level) - Self.spectrumFloor) / (Self.spectrumCeiling - Self.spectrumFloor)
        return plot.maxY - CGFloat(n.clamped(to: 0...1)) * plot.height
    }
}

struct EQGraphView: View {
    @Environment(AppModel.self) private var model
    var dbRange: Double = 12
    var interactive = true

    @State private var hoverLocation: CGPoint?
    @State private var hoveredBand: UUID?
    @State private var scrollMonitor: Any?
    @State private var magnifyStartQ: Double?
    @FocusState private var focused: Bool

    var body: some View {
        GeometryReader { geo in
            let mapper = GraphMapper(size: geo.size, dbRange: dbRange)
            ZStack(alignment: .topLeading) {
                GridLayer(mapper: mapper)
                SpectrumLayer(mapper: mapper)
                CurveLayer(mapper: mapper, settings: model.settings, selectedID: model.selectedBandID, sampleRate: model.engine.sampleRate)
                if interactive {
                    ForEach(Array(model.settings.bands.enumerated()), id: \.element.id) { index, band in
                        BandNode(band: band, index: index, mapper: mapper, hovered: $hoveredBand)
                    }
                    readout(mapper: mapper)
                }
            }
            .coordinateSpace(name: "eqgraph")
            .contentShape(Rectangle())
            .onContinuousHover(coordinateSpace: .named("eqgraph")) { phase in
                switch phase {
                case .active(let p): hoverLocation = p
                case .ended: hoverLocation = nil
                }
            }
            .gesture(SpatialTapGesture(count: 2, coordinateSpace: .named("eqgraph")).onEnded { tap in
                guard interactive, mapper.plot.contains(tap.location) else { return }
                model.addBand(frequency: mapper.frequency(atX: tap.location.x), gain: mapper.db(atY: tap.location.y).rounded(toPlaces: 1))
            })
            .simultaneousGesture(TapGesture().onEnded { focused = true })
            .gesture(MagnifyGesture().onChanged { g in
                guard let id = model.selectedBandID, let band = model.selectedBand else { return }
                if magnifyStartQ == nil { magnifyStartQ = band.q; model.checkpoint() }
                let q = (magnifyStartQ ?? 1) * Double(g.magnification)
                model.updateBand(id, undoable: false) { $0.q = q }
            }.onEnded { _ in magnifyStartQ = nil })
        }
        .instrumentPanel()
        .focusable(interactive)
        .focusEffectDisabled()
        .focused($focused)
        .onDeleteCommand {
            if let id = model.selectedBandID { model.removeBand(id) }
        }
        .onKeyPress(keys: [.upArrow, .downArrow, .leftArrow, .rightArrow]) { press in
            nudge(press.key, fine: press.modifiers.contains(.option))
        }
        .onAppear {
            model.analyzerAppeared()
            if interactive { installScrollMonitor() }
        }
        .onDisappear {
            model.analyzerDisappeared()
            if let scrollMonitor { NSEvent.removeMonitor(scrollMonitor) }
            scrollMonitor = nil
        }
    }

    @ViewBuilder
    private func readout(mapper: GraphMapper) -> some View {
        if let p = hoverLocation, mapper.plot.contains(p), hoveredBand == nil {
            Text("\(Format.frequency(mapper.frequency(atX: p.x)))   \(Format.gain(mapper.db(atY: p.y)))")
                .font(.system(size: 10, weight: .medium).monospacedDigit())
                .foregroundStyle(Theme.gridLabel)
                .padding(.horizontal, 6)
                .padding(.vertical, 3)
                .background(Theme.panelRaised.opacity(0.9), in: RoundedRectangle(cornerRadius: 4))
                .offset(x: mapper.plot.minX + 6, y: mapper.plot.minY + 4)
                .allowsHitTesting(false)
        }
    }

    private func nudge(_ key: KeyEquivalent, fine: Bool) -> KeyPress.Result {
        guard let id = model.selectedBandID else { return .ignored }
        let gainStep = fine ? 0.1 : 0.5
        let freqFactor = fine ? 1.01 : 1.06
        model.updateBand(id) { b in
            switch key {
            case .upArrow: b.gain += gainStep
            case .downArrow: b.gain -= gainStep
            case .rightArrow: b.frequency *= freqFactor
            case .leftArrow: b.frequency /= freqFactor
            default: break
            }
        }
        return .handled
    }

    /// Scroll wheel over a band (or the selected band) changes its Q.
    private func installScrollMonitor() {
        guard scrollMonitor == nil else { return }
        scrollMonitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { event in
            let precise = event.hasPreciseScrollingDeltas
            let delta = precise ? event.scrollingDeltaY * 0.006 : event.scrollingDeltaY * 0.06
            let began = event.phase == .began || !precise
            let consumed = MainActor.assumeIsolated { () -> Bool in
                guard hoverLocation != nil, let id = hoveredBand ?? model.selectedBandID else { return false }
                guard delta != 0 else { return true }
                if began { model.checkpoint() }
                model.updateBand(id, undoable: false) { $0.q *= exp(delta) }
                return true
            }
            return consumed ? nil : event
        }
    }
}

// MARK: - Layers

private struct GridLayer: View {
    let mapper: GraphMapper

    private static let minorFrequencies: [Double] = [30, 40, 60, 70, 80, 90, 300, 400, 600, 700, 800, 900, 3000, 4000, 6000, 7000, 8000, 9000]
    private static let majorFrequencies: [Double] = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10_000, 20_000]

    var body: some View {
        Canvas { ctx, _ in
            let plot = mapper.plot
            for f in Self.minorFrequencies {
                let x = mapper.x(f)
                ctx.stroke(Path { $0.move(to: CGPoint(x: x, y: plot.minY)); $0.addLine(to: CGPoint(x: x, y: plot.maxY)) }, with: .color(Theme.gridMinor), lineWidth: 0.5)
            }
            for f in Self.majorFrequencies {
                let x = mapper.x(f)
                ctx.stroke(Path { $0.move(to: CGPoint(x: x, y: plot.minY)); $0.addLine(to: CGPoint(x: x, y: plot.maxY)) }, with: .color(Theme.gridMajor), lineWidth: 0.5)
                let label = Text(Format.gridFrequency(f)).font(.system(size: 9, weight: .medium)).foregroundStyle(Theme.gridLabel)
                let anchor: UnitPoint = f == 20 ? .topLeading : (f == 20_000 ? .topTrailing : .top)
                ctx.draw(label, at: CGPoint(x: x, y: plot.maxY + 5), anchor: anchor)
            }
            let step: Double = mapper.dbRange <= 6 ? 1 : (mapper.dbRange <= 12 ? 3 : 6)
            var db = -mapper.dbRange
            while db <= mapper.dbRange + 0.001 {
                let y = mapper.y(db)
                let isZero = abs(db) < 0.001
                ctx.stroke(Path { $0.move(to: CGPoint(x: plot.minX, y: y)); $0.addLine(to: CGPoint(x: plot.maxX, y: y)) },
                           with: .color(isZero ? Color.white.opacity(0.22) : Theme.gridMinor), lineWidth: isZero ? 1 : 0.5)
                let label = Text(db > 0 ? "+\(Int(db))" : "\(Int(db))").font(.system(size: 9, weight: .medium).monospacedDigit()).foregroundStyle(Theme.gridLabel)
                ctx.draw(label, at: CGPoint(x: plot.minX - 6, y: y), anchor: .trailing)
                db += step
            }
        }
        .allowsHitTesting(false)
    }
}

private struct SpectrumLayer: View {
    @Environment(AppModel.self) private var model
    let mapper: GraphMapper

    var body: some View {
        let meters = model.meters
        Canvas { ctx, _ in
            let freqs = meters.spectrumFrequencies
            let plot = mapper.plot
            func path(_ levels: [Float]) -> Path? {
                guard levels.count == freqs.count, levels.count > 1 else { return nil }
                var p = Path()
                for i in levels.indices {
                    let pt = CGPoint(x: mapper.x(freqs[i]), y: mapper.spectrumY(levels[i]))
                    if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
                }
                return p
            }
            ctx.clip(to: Path(plot))
            if let pre = path(meters.spectrumPre) {
                ctx.stroke(pre, with: .color(Theme.spectrumPre), style: StrokeStyle(lineWidth: 1, lineJoin: .round))
            }
            if let post = path(meters.spectrumPost) {
                var fill = post
                fill.addLine(to: CGPoint(x: plot.maxX, y: plot.maxY))
                fill.addLine(to: CGPoint(x: plot.minX, y: plot.maxY))
                fill.closeSubpath()
                ctx.fill(fill, with: .linearGradient(
                    Gradient(colors: [Theme.spectrumPost.opacity(0.34), Theme.spectrumPost.opacity(0.03)]),
                    startPoint: CGPoint(x: 0, y: plot.minY), endPoint: CGPoint(x: 0, y: plot.maxY)))
                ctx.stroke(post, with: .color(Theme.spectrumPost.opacity(0.75)), style: StrokeStyle(lineWidth: 1, lineJoin: .round))
            }
        }
        .allowsHitTesting(false)
    }
}

private struct CurveLayer: View {
    let mapper: GraphMapper
    let settings: DSPSettings
    let selectedID: UUID?
    let sampleRate: Double

    var body: some View {
        Canvas { ctx, size in
            let plot = mapper.plot
            let count = max(2, Int(plot.width / 2))
            let freqs = (0..<count).map { mapper.frequency(atX: plot.minX + plot.width * CGFloat($0) / CGFloat(count - 1)) }
            ctx.clip(to: Path(plot.insetBy(dx: 0, dy: -2)))

            func curvePath(_ response: [Double]) -> Path {
                var p = Path()
                for (i, db) in response.enumerated() {
                    let pt = CGPoint(x: mapper.x(freqs[i]), y: mapper.y(db.clamped(to: -mapper.dbRange * 1.5...mapper.dbRange * 1.5)))
                    if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
                }
                return p
            }

            func filled(_ p: Path) -> Path {
                var f = p
                f.addLine(to: CGPoint(x: plot.maxX, y: mapper.y(0)))
                f.addLine(to: CGPoint(x: plot.minX, y: mapper.y(0)))
                f.closeSubpath()
                return f
            }

            // Selected band in its own colour.
            if let selectedID, let index = settings.bands.firstIndex(where: { $0.id == selectedID }) {
                let band = settings.bands[index]
                let color = Theme.bandColor(index)
                let response = ResponseCurve.responseDB(band: band, frequencies: freqs, sampleRate: sampleRate)
                let p = curvePath(response)
                ctx.fill(filled(p), with: .color(color.opacity(band.isEnabled ? 0.22 : 0.08)))
                ctx.stroke(p, with: .color(color.opacity(band.isEnabled ? 0.85 : 0.35)), style: StrokeStyle(lineWidth: 1.2, dash: band.isEnabled ? [] : [3, 3]))
            }

            // Total response, including tone macros: one curve, or one per channel
            // when bands target only the left or the right.
            let channels: [(EQChannels?, Color)] = settings.hasChannelSpecificBands
                ? [(.left, Theme.curve), (.right, Theme.curveRight)]
                : [(nil, Theme.curve)]
            for (channel, color) in channels {
                let total = ResponseCurve.responseDB(bands: settings.activeBands, frequencies: freqs, sampleRate: sampleRate, channel: channel)
                let p = curvePath(total)
                if settings.eqEnabled {
                    if channels.count == 1 { ctx.fill(filled(p), with: .color(.white.opacity(0.05))) }
                    var glow = ctx
                    glow.addFilter(.blur(radius: 4))
                    glow.stroke(p, with: .color(color.opacity(0.45)), lineWidth: 3)
                    ctx.stroke(p, with: .color(color), style: StrokeStyle(lineWidth: 1.8, lineJoin: .round))
                } else {
                    ctx.stroke(p, with: .color(color.opacity(0.35)), style: StrokeStyle(lineWidth: 1.5, dash: [5, 4]))
                }
                if let channel, let last = total.last {
                    let label = Text(channel == .left ? "L" : "R").font(.system(size: 10, weight: .bold)).foregroundStyle(color)
                    ctx.draw(label, at: CGPoint(x: plot.maxX - 8, y: mapper.y(last.clamped(to: -mapper.dbRange...mapper.dbRange)) - 9))
                }
            }
            _ = size
        }
        .allowsHitTesting(false)
    }
}

// MARK: - Nodes

private struct BandNode: View {
    @Environment(AppModel.self) private var model
    let band: EQBand
    let index: Int
    let mapper: GraphMapper
    @Binding var hovered: UUID?

    @State private var dragOrigin: (x: CGFloat, y: CGFloat)?

    private var isSelected: Bool { model.selectedBandID == band.id }
    private var color: Color { Theme.bandColor(index) }

    private var position: CGPoint {
        let y = band.type.usesGain ? mapper.y(band.gain.clamped(to: -mapper.dbRange...mapper.dbRange)) : mapper.y(0)
        return CGPoint(x: mapper.x(band.frequency), y: y)
    }

    var body: some View {
        let size: CGFloat = isSelected || hovered == band.id ? 18 : 15
        ZStack {
            Circle()
                .fill(band.isEnabled ? color : Theme.panelRaised)
                .overlay(Circle().strokeBorder(isSelected ? Color.white : (band.isEnabled ? Color.black.opacity(0.35) : color), lineWidth: isSelected ? 2 : 1.2))
                .shadow(color: color.opacity(isSelected ? 0.7 : 0.0), radius: 6)
            Text("\(index + 1)")
                .font(.system(size: 8.5, weight: .heavy, design: .rounded))
                .foregroundStyle(band.isEnabled ? Color.black.opacity(0.75) : color)
        }
        .frame(width: size, height: size)
        .contentShape(Circle().inset(by: -6))
        .position(position)
        .animation(.easeOut(duration: 0.12), value: isSelected)
        .onHover { inside in
            if inside { hovered = band.id } else if hovered == band.id { hovered = nil }
        }
        .gesture(drag)
        .onTapGesture {
            if NSEvent.modifierFlags.contains(.option) {
                model.removeBand(band.id)
            } else {
                model.selectedBandID = band.id
            }
        }
        .contextMenu { menu }
        .overlay(alignment: .topLeading) { label }
        .help(Text("Drag to move · Scroll or pinch for Q · ⌥-click to delete · Double-click empty space to add"))
    }

    /// Keeps the readout inside the plot. The plot can be narrower than the
    /// margins during the first layout pass, so never build an inverted range.
    private var labelX: CGFloat {
        let lo = mapper.plot.minX + 70
        let hi = max(lo, mapper.plot.maxX - 70)
        return min(max(position.x, lo), hi)
    }

    @ViewBuilder
    private var label: some View {
        if isSelected || hovered == band.id {
            let text = band.type.usesGain
                ? "\(Format.frequency(band.frequency))  \(Format.gain(band.gain))  Q \(Format.q(band.q))"
                : "\(Format.frequency(band.frequency))  Q \(Format.q(band.q))"
            Text(text)
                .font(.system(size: 10, weight: .semibold).monospacedDigit())
                .foregroundStyle(.white)
                .padding(.horizontal, 6)
                .padding(.vertical, 3)
                .background(color.opacity(0.85), in: RoundedRectangle(cornerRadius: 4))
                .fixedSize()
                .position(x: labelX, y: position.y > mapper.plot.minY + 30 ? position.y - 22 : position.y + 24)
                .allowsHitTesting(false)
        }
    }

    private var drag: some Gesture {
        DragGesture(minimumDistance: 1, coordinateSpace: .named("eqgraph"))
            .onChanged { g in
                if dragOrigin == nil {
                    model.checkpoint()
                    model.selectedBandID = band.id
                    dragOrigin = (position.x, position.y)
                }
                guard let origin = dragOrigin else { return }
                let fine: CGFloat = NSEvent.modifierFlags.contains(.option) ? 0.2 : 1
                let x = origin.x + g.translation.width * fine
                let y = origin.y + g.translation.height * fine
                let f = mapper.frequency(atX: x)
                let gain = mapper.db(atY: y)
                let usesGain = band.type.usesGain
                model.updateBand(band.id, undoable: false) { b in
                    b.frequency = f
                    if usesGain { b.gain = gain.rounded(toPlaces: 1) }
                }
            }
            .onEnded { _ in dragOrigin = nil }
    }

    @ViewBuilder
    private var menu: some View {
        Picker("Filter Type", selection: Binding(get: { band.type }, set: { t in model.updateBand(band.id) { $0.type = t } })) {
            ForEach(FilterType.allCases) { t in Text(t.displayName).tag(t) }
        }
        Button(band.isEnabled ? "Disable Band" : "Enable Band") {
            model.updateBand(band.id) { $0.isEnabled.toggle() }
        }
        Button("Reset Gain") { model.updateBand(band.id) { $0.gain = 0 } }
        Divider()
        Button("Delete Band", role: .destructive) { model.removeBand(band.id) }
    }
}

extension Double {
    func rounded(toPlaces places: Int) -> Double {
        let p = pow(10, Double(places))
        return (self * p).rounded() / p
    }
}
