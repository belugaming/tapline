import SwiftUI
import TaplineCore

struct MenuBarPanel: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let running = model.status == .running
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Image(systemName: "slider.vertical.3")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(running ? Theme.accent : .secondary)
                VStack(alignment: .leading, spacing: 0) {
                    Text("Tapline").font(.headline)
                    Text(StatusText.short(for: model.status))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Toggle("Processing", isOn: Binding(get: { model.powerOn }, set: { _ in model.togglePower() }))
                    .toggleStyle(.switch)
                    .labelsHidden()
            }

            if model.processes.conflictingAppRunning {
                Label("Quit SoundSource to avoid double processing", systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(Theme.warning)
            }
            if model.status == .permissionDenied {
                Button("Allow System Audio Recording…") { NSWorkspace.shared.open(AudioCapturePermission.settingsURL) }
            }

            Picker(selection: Binding(get: { model.devices.defaultOutputID }, set: { id in
                if let d = model.devices.outputDevices.first(where: { $0.id == id }) { model.devices.setDefaultOutput(d) }
            })) {
                ForEach(model.devices.outputDevices) { d in
                    Text(d.name).tag(d.id)
                }
            } label: {
                Image(systemName: model.currentOutput?.symbolName ?? "speaker.wave.2")
            }

            HStack(spacing: 8) {
                Image(systemName: model.devices.outputMuted ? "speaker.slash.fill" : "speaker.wave.2.fill")
                    .foregroundStyle(.secondary)
                    .frame(width: 18)
                Slider(value: Binding(get: { Double(model.devices.outputVolume) }, set: { model.devices.setOutputVolume(Float($0)) }), in: 0...1)
                    .disabled(model.currentOutput?.hasOutputVolume != true)
            }

            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Toggle("EQ", isOn: Binding(get: { model.settings.eqEnabled }, set: { model.setEQEnabled($0) }))
                        .toggleStyle(.switch)
                        .controlSize(.small)
                    Spacer()
                    PresetMenu()
                        .controlSize(.small)
                        .fixedSize()
                }
                CurveThumbnail(bands: model.settings.bands, tone: model.settings.tone, range: 12, color: model.settings.eqEnabled ? Theme.accent : .gray)
                    .frame(height: 56)
                    .padding(6)
                    .instrumentPanel(radius: 8)
                    .onTapGesture { AppDelegate.showMainWindow() }
            }

            HStack(spacing: 8) {
                Toggle("Listen as", isOn: Binding(get: { model.simulating }, set: { model.setSimulating($0) }))
                    .toggleStyle(.switch)
                    .controlSize(.small)
                    .disabled(model.simulation == nil)
                Text(model.simulation?.title ?? String(localized: "Choose a device in Assistant"))
                    .font(.callout)
                    .foregroundStyle(model.simulating ? Theme.accent : .secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(Text(model.simulation?.detail ?? ""))
                Spacer(minLength: 0)
            }

            let playing = model.processes.apps.filter(\.isPlaying)
            if !playing.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Playing").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    ForEach(playing.prefix(6)) { app in
                        AppRow(app: app, compact: true)
                    }
                }
            }

            Divider()
            HStack {
                Button("Open Tapline") { AppDelegate.showMainWindow() }
                    .keyboardShortcut(",")
                Spacer()
                Button {
                    NSApp.terminate(nil)
                } label: {
                    Image(systemName: "power")
                }
                .buttonStyle(.borderless)
                .help(Text("Quit Tapline"))
            }
        }
        .padding(14)
        .frame(width: 340)
    }
}
