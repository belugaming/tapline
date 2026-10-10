import SwiftUI
import TaplineCore

/// Rotary control. Drag up/down to change, ⌥ for fine steps, double-click to reset,
/// click the value to type a number.
struct Knob: View {
    enum Scale { case linear, logarithmic }

    let title: LocalizedStringKey
    @Binding var value: Double
    let range: ClosedRange<Double>
    var defaultValue: Double
    var scale: Scale = .linear
    var color: Color = Theme.accent
    var bipolar = false
    var size: CGFloat = 54
    var format: (Double) -> String = { String(format: "%.1f", $0) }
    var parse: (String) -> Double? = { Double($0.filter { "0123456789.-".contains($0) }) }
    var onEditingChanged: (Bool) -> Void = { _ in }

    @State private var dragStart: Double?
    @State private var editing = false
    @State private var text = ""
    @FocusState private var fieldFocused: Bool

    private let sweep = 270.0

    private func normalized(_ v: Double) -> Double {
        switch scale {
        case .linear: (v - range.lowerBound) / (range.upperBound - range.lowerBound)
        case .logarithmic: log(v / range.lowerBound) / log(range.upperBound / range.lowerBound)
        }
    }

    private func denormalized(_ n: Double) -> Double {
        let n = n.clamped(to: 0...1)
        switch scale {
        case .linear: return range.lowerBound + n * (range.upperBound - range.lowerBound)
        case .logarithmic: return range.lowerBound * pow(range.upperBound / range.lowerBound, n)
        }
    }

    var body: some View {
        VStack(spacing: 6) {
            Text(title)
                .font(.system(size: 10, weight: .semibold))
                .textCase(.uppercase)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            dial
            valueLabel
        }
        .frame(minWidth: size + 12)
    }

    private var dial: some View {
        let n = normalized(value).clamped(to: 0...1)
        let start = 90 + (360 - sweep) / 2
        let origin = bipolar ? normalized(bipolarZero) : 0
        return ZStack {
            Circle()
                .trim(from: 0, to: sweep / 360)
                .rotation(.degrees(start))
                .stroke(Color.primary.opacity(0.12), style: StrokeStyle(lineWidth: 4, lineCap: .round))
            Circle()
                .trim(from: min(origin, n) * sweep / 360, to: max(origin, n) * sweep / 360)
                .rotation(.degrees(start))
                .stroke(color, style: StrokeStyle(lineWidth: 4, lineCap: .round))
            Circle()
                .fill(.background.secondary)
                .overlay(Circle().strokeBorder(Color.primary.opacity(0.12)))
                .padding(8)
                .shadow(color: .black.opacity(0.18), radius: 2, y: 1)
            Capsule()
                .fill(Color.primary.opacity(0.8))
                .frame(width: 2.5, height: size * 0.18)
                .offset(y: -size * 0.2)
                .rotationEffect(.degrees(start + 90 + n * sweep))
        }
        .frame(width: size, height: size)
        .contentShape(Circle())
        .gesture(
            DragGesture(minimumDistance: 0)
                .onChanged { g in
                    if dragStart == nil {
                        dragStart = normalized(value)
                        onEditingChanged(true)
                    }
                    let fine = NSEvent.modifierFlags.contains(.option)
                    let delta = Double(-g.translation.height + g.translation.width * 0.25) / (fine ? 900 : 180)
                    value = denormalized((dragStart ?? 0) + delta)
                }
                .onEnded { _ in
                    dragStart = nil
                    onEditingChanged(false)
                }
        )
        .simultaneousGesture(TapGesture(count: 2).onEnded {
            onEditingChanged(true)
            value = defaultValue
            onEditingChanged(false)
        })
        .accessibilityElement()
        .accessibilityLabel(Text(title))
        .accessibilityValue(format(value))
        .accessibilityAdjustableAction { direction in
            let step = 0.02
            let n = normalized(value)
            value = denormalized(direction == .increment ? n + step : n - step)
        }
    }

    private var bipolarZero: Double { range.contains(0) ? 0 : range.lowerBound }

    @ViewBuilder
    private var valueLabel: some View {
        if editing {
            TextField("", text: $text)
                .textFieldStyle(.plain)
                .multilineTextAlignment(.center)
                .font(.system(size: 11, weight: .medium).monospacedDigit())
                .frame(width: size + 10)
                .focused($fieldFocused)
                .onSubmit(commit)
                .onChange(of: fieldFocused) { _, focused in if !focused { commit() } }
                .onAppear { fieldFocused = true }
        } else {
            Text(format(value))
                .font(.system(size: 11, weight: .medium).monospacedDigit())
                .foregroundStyle(.primary.opacity(0.85))
                .lineLimit(1)
                .onTapGesture {
                    text = format(value)
                    editing = true
                }
        }
    }

    private func commit() {
        if let v = parse(text) {
            onEditingChanged(true)
            value = v.clamped(to: range)
            onEditingChanged(false)
        }
        editing = false
    }
}

enum KnobParsers {
    /// Accepts "1.2k", "1200", "1,2 kHz".
    static func frequency(_ s: String) -> Double? {
        let lower = s.lowercased().replacingOccurrences(of: ",", with: ".")
        let k = lower.contains("k")
        guard let v = Double(lower.filter { "0123456789.".contains($0) }) else { return nil }
        return k ? v * 1000 : v
    }
}
