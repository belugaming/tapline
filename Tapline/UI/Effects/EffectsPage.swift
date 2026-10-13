import AppKit
import SwiftUI
import TaplineCore
import UniformTypeIdentifiers

struct EffectsPage: View {
    @Environment(AppModel.self) private var model
    @State private var importMessage: String?

    var body: some View {
        let fx = model.settings.effects
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Effects apply to everything playing through \(model.currentOutput?.name ?? String(localized: "this device")) and are saved per device.")
                    .foregroundStyle(.secondary)

                LazyVGrid(columns: [GridItem(.adaptive(minimum: 380), spacing: 16, alignment: .top)], spacing: 16) {
                    Card(title: "Limiter", subtitle: "Catches peaks so boosts never clip. Leave it on unless you are measuring.", systemImage: "waveform.path.badge.minus") {
                        Toggle("Enabled", isOn: binding(\.limiterEnabled, fx.limiterEnabled))
                            .toggleStyle(.switch)
                        LabeledSlider(title: "Ceiling", value: binding(\.limiterCeilingDB, fx.limiterCeilingDB), range: -12...0, format: { Format.gain($0) })
                            .disabled(!fx.limiterEnabled)
                        HStack {
                            Text("Gain reduction").font(.caption).foregroundStyle(.secondary)
                            GainReductionBar(db: model.meters.gainReductionDB)
                                .rotationEffect(.degrees(-90))
                                .frame(width: 8, height: 120)
                                .frame(width: 120, height: 8)
                            Text(String(format: "%.1f dB", model.meters.gainReductionDB))
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                        }
                    }

                    Card(title: "Output Gain", subtitle: "Overall level after the EQ and effects.", systemImage: "speaker.plus") {
                        LabeledSlider(title: "Gain", value: binding(\.outputGainDB, fx.outputGainDB), range: -24...12, format: { Format.gain($0) })
                    }

                    Card(title: "Balance", subtitle: "Shift the stereo image left or right.", systemImage: "arrow.left.and.right") {
                        LabeledSlider(title: "Balance", value: binding(\.balance, fx.balance), range: -1...1, format: { v in
                            abs(v) < 0.01 ? String(localized: "Center") : (v < 0 ? String(format: "L %.0f", -v * 100) : String(format: "R %.0f", v * 100))
                        })
                    }

                    Card(title: "Stereo Width", subtitle: "Narrow or widen the stereo image. 0% is mono.", systemImage: "arrow.left.and.right.square") {
                        LabeledSlider(title: "Width", value: binding(\.width, fx.width), range: 0...2, format: { Format.percent($0) })
                            .disabled(fx.mono)
                        Toggle("Mono", isOn: binding(\.mono, fx.mono))
                        Toggle("Swap left and right", isOn: binding(\.swapChannels, fx.swapChannels))
                    }

                    Card(title: "Headphone Crossfeed", subtitle: "Blends a little of each channel into the other, like listening to speakers. Reduces fatigue on hard-panned recordings.", systemImage: "headphones") {
                        Toggle("Enabled", isOn: Binding(get: { fx.crossfeedEnabled }, set: { model.setCrossfeed($0) }))
                            .toggleStyle(.switch)
                        LabeledSlider(title: "Amount", value: binding(\.crossfeed, fx.crossfeed), range: 0...1, format: { Format.percent($0) })
                            .disabled(!fx.crossfeedEnabled)
                    }

                    virtualSpeakersCard(fx)
                }

                HStack {
                    Spacer()
                    Button("Reset Effects") {
                        model.mutate { $0.effects = EffectsSettings() }
                    }
                }
            }
            .padding(20)
            .frame(maxWidth: 980)
            .frame(maxWidth: .infinity)
        }
    }

    private func virtualSpeakersCard(_ fx: EffectsSettings) -> some View {
        let current = model.currentVirtualSpeakers
        return Card(title: "Virtual Speakers", subtitle: "Puts the sound in front of you, like a pair of speakers at ±30°, with binaural impulse responses. For headphones; replaces crossfeed.", systemImage: "hifispeaker.2") {
            Toggle("Enabled", isOn: Binding(get: { fx.virtualSpeakersEnabled }, set: { model.setVirtualSpeakers($0) }))
                .toggleStyle(.switch)
            HStack {
                Picker("Speakers", selection: Binding(get: { current.id }, set: { model.chooseVirtualSpeakers($0) })) {
                    Section("Built in") {
                        ForEach(VirtualSpeakerLibrary.builtIn) { Text($0.name).tag($0.id) }
                    }
                    if !model.speakers.imported.isEmpty {
                        Section("Imported") {
                            ForEach(model.speakers.imported) { Text($0.name).tag($0.id) }
                        }
                    }
                }
                .frame(maxWidth: 300)
                Button("Import…") { importSpeakers() }
                    .help("A HeSuVi or Impulcifer WAV file, a 4-channel file, or two stereo files (one per speaker)")
            }
            Text(Self.detail(current))
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                if !current.isBuiltIn {
                    Button("Remove “\(current.name)”", role: .destructive) { model.removeVirtualSpeakers(current.id) }
                }
                Spacer()
                Link("Where to find HRIR files", destination: URL(string: "https://github.com/belugaming/tapline#virtual-speakers")!)
                    .font(.caption)
            }
            if let problem = model.virtualSpeakersProblem ?? importMessage {
                Label(problem, systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private static func detail(_ set: VirtualSpeakerSet) -> String {
        switch set.id {
        case EffectsSettings.defaultVirtualSpeakers:
            return String(localized: "Neumann KU100 dummy head from the SADIE II database, with the reflections of a treated studio room. Bass and lower mids stay dry.")
        case "sadie-ku100":
            return String(localized: "Neumann KU100 dummy head from the SADIE II database, anechoic: the speakers without a room. The cleanest sound, but it may seem closer to your head.")
        default:
            let layout: String = switch set.layout {
            case .hesuvi14: String(localized: "HeSuVi, 14 channels")
            case .hesuvi7: String(localized: "HeSuVi, 7 channels")
            case .impulcifer16: String(localized: "Impulcifer hrir.wav")
            case .fourChannel: String(localized: "4 channels")
            case .stereoPair: String(localized: "Two stereo files")
            case .mirroredStereo: String(localized: "One speaker, mirrored")
            }
            let rate = Format.sampleRate(set.sampleRate)
            let length = String(format: "%.0f ms", set.duration * 1000)
            return "\(layout) · \(rate) · \(length)"
        }
    }

    private func importSpeakers() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.wav]
        panel.allowsMultipleSelection = true
        panel.message = String(localized: "Choose a HeSuVi or Impulcifer file, or two stereo files (one per speaker).")
        guard panel.runModal() == .OK, !panel.urls.isEmpty else { return }
        do {
            try model.importVirtualSpeakers(from: panel.urls)
            importMessage = nil
        } catch {
            importMessage = VirtualSpeakerLibrary.message(for: error)
        }
    }

    private func binding<T: Equatable>(_ key: WritableKeyPath<EffectsSettings, T>, _ current: T) -> Binding<T> {
        Binding(get: { current }, set: { v in model.mutate { $0.effects[keyPath: key] = v } })
    }
}

struct LabeledSlider: View {
    let title: LocalizedStringKey
    @Binding var value: Double
    let range: ClosedRange<Double>
    let format: (Double) -> String

    var body: some View {
        HStack {
            Text(title)
                .frame(width: 80, alignment: .leading)
            Slider(value: $value, in: range)
            Text(format(value))
                .font(.callout.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: 70, alignment: .trailing)
        }
    }
}
