import CoreAudio
import SwiftUI
import TaplineCore

struct DevicesPage: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Card(title: "Output", subtitle: "Tapline follows the system output. Each device keeps its own EQ, so plugging in your headphones switches the curve automatically.", systemImage: "hifispeaker.2") {
                    VStack(spacing: 6) {
                        ForEach(model.devices.outputDevices) { device in
                            DeviceRow(device: device, isDefault: device.id == model.devices.defaultOutputID, output: true)
                        }
                    }
                }
                Card(title: "Input", systemImage: "mic") {
                    VStack(spacing: 6) {
                        ForEach(model.devices.inputDevices) { device in
                            DeviceRow(device: device, isDefault: device.id == model.devices.defaultInputID, output: false)
                        }
                    }
                }
                EngineInfoCard()
            }
            .padding(20)
            .frame(maxWidth: 900)
            .frame(maxWidth: .infinity)
        }
    }
}

private struct DeviceRow: View {
    @Environment(AppModel.self) private var model
    let device: AudioDevice
    let isDefault: Bool
    let output: Bool

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: device.symbolName)
                .font(.title3)
                .frame(width: 28)
                .foregroundStyle(isDefault ? Theme.accent : .secondary)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(device.name).font(.body.weight(isDefault ? .semibold : .regular))
                    if isDefault {
                        Text("Current")
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 6).padding(.vertical, 1)
                            .background(Theme.accent.opacity(0.2), in: Capsule())
                    }
                }
                Text(details)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if output {
                let profile = model.settings(forDeviceUID: device.uid)
                Text(profile.eqEnabled ? (BuiltInPresets.all + model.userPresets).first { $0.matches(profile) }?.name ?? String(localized: "Custom EQ") : String(localized: "EQ off"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .frame(maxWidth: 160, alignment: .trailing)
            }
            if device.availableSampleRates.count > 1 {
                Picker("Sample Rate", selection: Binding(get: { device.sampleRate }, set: { model.devices.setSampleRate($0, for: device) })) {
                    ForEach(device.availableSampleRates, id: \.self) { rate in
                        Text(String(format: "%g kHz", rate / 1000)).tag(rate)
                    }
                }
                .labelsHidden()
                .frame(width: 100)
                .help(Text("Sample rate"))
            }
            Button(isDefault ? "In Use" : "Use") {
                if output { model.devices.setDefaultOutput(device) } else { model.devices.setDefaultInput(device) }
            }
            .disabled(isDefault)
            .frame(width: 70)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(isDefault ? Theme.accent.opacity(0.07) : Color.primary.opacity(0.03), in: RoundedRectangle(cornerRadius: 10))
    }

    private var details: String {
        let channels = output ? device.outputChannels : device.inputChannels
        var parts = [device.transportName, String(format: String(localized: "%d ch"), channels)]
        if !device.manufacturer.isEmpty { parts.insert(device.manufacturer, at: 0) }
        return parts.joined(separator: " · ")
    }
}

private struct EngineInfoCard: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Card(title: "Audio Engine", systemImage: "cpu") {
            Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 6) {
                GridRow {
                    Text("Status").foregroundStyle(.secondary)
                    Text(StatusText.short(for: model.status))
                }
                GridRow {
                    Text("Sample rate").foregroundStyle(.secondary)
                    Text(String(format: "%g kHz", model.engine.sampleRate / 1000))
                }
                GridRow {
                    Text("Added latency").foregroundStyle(.secondary)
                    Text(String(format: "%.1f ms", Double(model.engine.latencyFrames) / max(model.engine.sampleRate, 1) * 1000))
                }
                GridRow {
                    Text("Tapped streams").foregroundStyle(.secondary)
                    Text("\(model.engine.sources.count)")
                }
                GridRow {
                    Text("Dropouts").foregroundStyle(.secondary)
                    Text("\(model.overloadCount)")
                }
            }
            .font(.callout)
            DisclosureGroup("Diagnostics") {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(model.engine.diagnostics, id: \.self) { line in
                        Text(line).font(.caption.monospaced()).textSelection(.enabled)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            HStack {
                Button("Restart Audio Engine") { model.restartEngine() }
                    .disabled(!model.powerOn)
                Spacer()
            }
        }
    }
}
