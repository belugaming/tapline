import SwiftUI
import TaplineCore

struct MixerPage: View {
    @Environment(AppModel.self) private var model
    @State private var showBackground = false

    private var playing: [AudioApp] { model.processes.apps.filter { $0.isPlaying } }
    private var idleApps: [AudioApp] { model.processes.apps.filter { !$0.isPlaying && ($0.isRegularApp || model.appSettings[$0.id] != nil) } }
    private var background: [AudioApp] { model.processes.apps.filter { !$0.isPlaying && !$0.isRegularApp && model.appSettings[$0.id] == nil } }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                if model.processes.conflictingAppRunning {
                    ConflictBanner()
                }
                if model.status != .running {
                    EngineOffBanner()
                }
                OutputVolumeCard()

                section(title: "Playing Now", apps: playing, empty: "Nothing is playing right now")
                section(title: "Other Apps", apps: idleApps, empty: nil)

                if !background.isEmpty {
                    DisclosureGroup(isExpanded: $showBackground) {
                        VStack(spacing: 6) {
                            ForEach(background) { AppRow(app: $0) }
                        }
                        .padding(.top, 6)
                    } label: {
                        Text("Background Processes (\(background.count))")
                            .font(.headline)
                    }
                }

                Text("Volume changes apply on top of each app's own volume. Bypassed apps play directly to the output without EQ or effects, which is useful for music production software.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(20)
            .frame(maxWidth: 820, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
    }

    @ViewBuilder
    private func section(title: LocalizedStringKey, apps: [AudioApp], empty: LocalizedStringKey?) -> some View {
        if !apps.isEmpty || empty != nil {
            VStack(alignment: .leading, spacing: 8) {
                Text(title).font(.headline)
                if apps.isEmpty, let empty {
                    Text(empty)
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, minHeight: 50)
                        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 12))
                } else {
                    VStack(spacing: 6) {
                        ForEach(apps) { AppRow(app: $0) }
                    }
                }
            }
        }
    }
}

struct AppRow: View {
    @Environment(AppModel.self) private var model
    let app: AudioApp
    var compact = false

    var body: some View {
        let s = model.appSetting(for: app)
        let custom = model.appSettings[app.id] != nil
        HStack(spacing: compact ? 8 : 12) {
            AppIcon(app: app, size: compact ? 20 : 28)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text(app.name)
                        .font(compact ? .callout : .body.weight(.medium))
                        .lineLimit(1)
                    if app.isPlaying {
                        Circle().fill(Theme.accent).frame(width: 6, height: 6)
                            .help(Text("Playing"))
                    }
                    if s.bypass {
                        Text("Bypass")
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 5)
                            .padding(.vertical, 1)
                            .background(Theme.warning.opacity(0.25), in: Capsule())
                    }
                }
                if !compact {
                    if custom && !s.bypass {
                        MiniMeter(level: model.meters.appPeaks[app.id] ?? 0)
                            .frame(maxWidth: 200)
                    } else if !s.bypass {
                        Text("In system mix")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                    } else {
                        Text("Plays directly, unprocessed")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                    }
                }
            }
            .frame(width: compact ? 96 : 220, alignment: .leading)

            Button {
                model.setMuted(!s.muted, for: app)
            } label: {
                Image(systemName: s.muted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                    .frame(width: 18)
                    .foregroundStyle(s.muted ? Theme.danger : .secondary)
            }
            .buttonStyle(.borderless)
            .disabled(s.bypass)
            .help(s.muted ? Text("Unmute") : Text("Mute"))

            VolumeSlider(value: Binding(get: { s.volume }, set: { model.setVolume($0, for: app) }), minWidth: compact ? 60 : 120)
                .disabled(s.bypass)
                .opacity(s.bypass ? 0.4 : 1)

            Text(Format.percent(s.volume))
                .font(.callout.monospacedDigit())
                .foregroundStyle(s.bypass ? .tertiary : .secondary)
                .frame(width: compact ? 38 : 44, alignment: .trailing)

            if !compact {
                Menu {
                    Toggle("Bypass Tapline", isOn: Binding(get: { s.bypass }, set: { model.setBypass($0, for: app) }))
                    Button("Reset") { model.resetApp(app) }
                        .disabled(!custom)
                } label: {
                    Image(systemName: "ellipsis")
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
            }
        }
        .padding(.horizontal, compact ? 4 : 12)
        .padding(.vertical, compact ? 3 : 8)
        .background(compact ? Color.clear : Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 10))
    }
}

/// 0-200% slider that snaps to 100%.
struct VolumeSlider: View {
    @Binding var value: Double
    var minWidth: CGFloat = 120

    var body: some View {
        Slider(value: Binding(get: { value }, set: { v in
            value = abs(v - 1) < 0.04 ? 1 : v
        }), in: 0...2)
        .overlay(alignment: .center) {
            GeometryReader { geo in
                Rectangle()
                    .fill(Color.primary.opacity(0.25))
                    .frame(width: 1, height: 8)
                    .position(x: geo.size.width / 2, y: geo.size.height / 2 + 9)
            }
            .allowsHitTesting(false)
        }
        .frame(minWidth: minWidth)
    }
}

struct AppIcon: View {
    @Environment(AppModel.self) private var model
    let app: AudioApp
    var size: CGFloat = 28

    var body: some View {
        Group {
            if let icon = model.processes.icon(for: app) {
                Image(nsImage: icon).resizable()
            } else {
                Image(systemName: "app.dashed")
                    .resizable()
                    .padding(size * 0.15)
                    .foregroundStyle(.secondary)
            }
        }
        .frame(width: size, height: size)
    }
}

private struct OutputVolumeCard: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let device = model.currentOutput
        HStack(spacing: 12) {
            Image(systemName: device?.symbolName ?? "speaker.wave.2")
                .font(.title2)
                .frame(width: 28)
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(device?.name ?? String(localized: "No Output"))
                    .font(.body.weight(.semibold))
                Text("Output volume")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .frame(width: 220, alignment: .leading)
            Button {
                model.devices.setOutputMuted(!model.devices.outputMuted)
            } label: {
                Image(systemName: model.devices.outputMuted ? "speaker.slash.fill" : "speaker.wave.3.fill")
                    .frame(width: 18)
            }
            .buttonStyle(.borderless)
            .disabled(device?.hasOutputVolume != true)
            Slider(value: Binding(get: { Double(model.devices.outputVolume) }, set: { model.devices.setOutputVolume(Float($0)) }), in: 0...1)
                .disabled(device?.hasOutputVolume != true)
            Text(device?.hasOutputVolume == true ? Format.percent(Double(model.devices.outputVolume)) : "—")
                .font(.callout.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: 44, alignment: .trailing)
            Color.clear.frame(width: 18)
        }
        .padding(12)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.separator.opacity(0.6)))
        .help(device?.hasOutputVolume == true ? Text("System volume of the current output") : Text("This device has no software volume control. Use its hardware knob."))
    }
}

struct ConflictBanner: View {
    var body: some View {
        Label {
            VStack(alignment: .leading, spacing: 2) {
                Text("SoundSource or another Rogue Amoeba app is running").font(.callout.weight(.semibold))
                Text("Quit it while using Tapline, otherwise both apps process the same audio.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        } icon: {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Theme.warning)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.warning.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
    }
}

struct EngineOffBanner: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        HStack {
            Label {
                Text(StatusText.message(for: model.status))
                    .font(.callout)
            } icon: {
                Image(systemName: "power").foregroundStyle(.secondary)
            }
            Spacer()
            if model.status == .permissionDenied || model.status == .requestingPermission {
                Button("Open Privacy Settings") { NSWorkspace.shared.open(AudioCapturePermission.settingsURL) }
                Button("Try Again") { model.retryPermission() }
            } else if !model.powerOn {
                Button("Turn On") { model.togglePower() }
                    .buttonStyle(.borderedProminent)
            } else {
                Button("Restart Audio") { model.restartEngine() }
            }
        }
        .padding(12)
        .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 10))
    }
}

enum StatusText {
    static func message(for status: AppModel.Status) -> LocalizedStringKey {
        switch status {
        case .off: "Tapline is off. Audio plays normally without processing."
        case .requestingPermission: "Waiting for the System Audio Recording permission…"
        case .permissionDenied: "Tapline needs the System Audio Recording permission to process sound."
        case .running: "Running"
        case .noOutput: "No output device available."
        case .failed(let message): "Audio engine error: \(message)"
        }
    }

    static func short(for status: AppModel.Status) -> LocalizedStringKey {
        switch status {
        case .off: "Off"
        case .requestingPermission: "Waiting for permission"
        case .permissionDenied: "Permission needed"
        case .running: "Active"
        case .noOutput: "No output"
        case .failed: "Error"
        }
    }
}
