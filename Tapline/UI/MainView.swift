import SwiftUI
import TaplineCore

enum Page: String, CaseIterable, Identifiable {
    case equalizer, mixer, assistant, room, effects, devices, settings

    var id: String { rawValue }

    var title: LocalizedStringKey {
        switch self {
        case .equalizer: "Equalizer"
        case .mixer: "Mixer"
        case .assistant: "Assistant"
        case .room: "Room"
        case .effects: "Effects"
        case .devices: "Devices"
        case .settings: "Settings"
        }
    }

    var symbol: String {
        switch self {
        case .equalizer: "slider.vertical.3"
        case .mixer: "speaker.wave.2"
        case .assistant: "wand.and.stars"
        case .room: "waveform.badge.mic"
        case .effects: "dial.medium"
        case .devices: "hifispeaker.2"
        case .settings: "gearshape"
        }
    }
}

struct MainView: View {
    @Environment(AppModel.self) private var model
    @Environment(SquigService.self) private var squig
    @Environment(\.openWindow) private var openWindow
    @AppStorage(DebugSnapshot.pageKey) private var page: Page = .equalizer

    var body: some View {
        NavigationSplitView {
            List(selection: Binding(get: { Optional(page) }, set: { if let p = $0 { page = p } })) {
                ForEach(Page.allCases) { p in
                    // Explicit colours: the default vibrant sidebar tint disappears while the window is dragged.
                    Label {
                        Text(p.title)
                    } icon: {
                        Image(systemName: p.symbol)
                            .foregroundStyle(page == p ? Color.white : Color.accentColor)
                    }
                    .tag(p)
                }
            }
            .safeAreaInset(edge: .bottom) { PowerCard().padding(10) }
            .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 280)
        } detail: {
            VStack(spacing: 0) {
                if page == .equalizer || page == .assistant || page == .effects || page == .room {
                    if model.processes.conflictingAppRunning {
                        ConflictBanner().padding(.horizontal, 16).padding(.top, 10)
                    } else if model.status != .running {
                        EngineOffBanner().padding(.horizontal, 16).padding(.top, 10)
                    }
                }
                Group {
                    switch page {
                    case .equalizer: EqualizerPage()
                    case .mixer: MixerPage()
                    case .assistant: AssistantPage()
                    case .room: RoomPage()
                    case .effects: EffectsPage()
                    case .devices: DevicesPage()
                    case .settings: SettingsPage()
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .navigationTitle(page.title)
        }
        .toolbar {
            ToolbarItem(placement: .navigation) {
                OutputDeviceMenu()
            }
            ToolbarItemGroup(placement: .primaryAction) {
                Button { model.undo() } label: { Label("Undo", systemImage: "arrow.uturn.backward") }
                    .disabled(!model.canUndo)
                    .help(Text("Undo (⌘Z)"))
                Button { model.redo() } label: { Label("Redo", systemImage: "arrow.uturn.forward") }
                    .disabled(!model.canRedo)
                    .help(Text("Redo (⇧⌘Z)"))
            }
        }
        .onAppear {
            OpenWindowBridge.shared.open = { openWindow(id: $0) }
            DebugSnapshot.runSquigSelfTest(squig)
            if DebugSnapshot.isActive {
                let model = model
                DebugSnapshot.makeMenuPanel = { NSHostingView(rootView: MenuBarPanel().environment(model)) }
            }
        }
    }
}

struct OutputDeviceMenu: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Menu {
            ForEach(model.devices.outputDevices) { device in
                Button {
                    model.devices.setDefaultOutput(device)
                } label: {
                    if device.id == model.devices.defaultOutputID {
                        Label(device.name, systemImage: "checkmark")
                    } else {
                        Label(device.name, systemImage: device.symbolName)
                    }
                }
            }
        } label: {
            Label {
                Text(model.currentOutput?.name ?? String(localized: "No Output"))
            } icon: {
                Image(systemName: model.currentOutput?.symbolName ?? "speaker.slash")
            }
            .labelStyle(.titleAndIcon)
        }
        .help(Text("Output device"))
    }
}

/// Power switch, status and output meters at the bottom of the sidebar.
struct PowerCard: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let running = model.status == .running
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                Button {
                    model.togglePower()
                } label: {
                    Image(systemName: "power")
                        .font(.system(size: 15, weight: .bold))
                        .foregroundStyle(running ? Color.black.opacity(0.8) : .secondary)
                        .frame(width: 34, height: 34)
                        .background(running ? Theme.accent : Color.primary.opacity(0.08), in: Circle())
                        .shadow(color: running ? Theme.accent.opacity(0.6) : .clear, radius: 6)
                }
                .buttonStyle(.plain)
                .help(model.powerOn ? Text("Turn processing off") : Text("Turn processing on"))
                VStack(alignment: .leading, spacing: 1) {
                    Text("Tapline").font(.headline)
                    Text(StatusText.short(for: model.status))
                        .font(.caption)
                        .foregroundStyle(running ? Theme.accent : .secondary)
                }
                Spacer()
            }
            if running {
                VStack(spacing: 3) {
                    MeterBar(peak: model.meters.peakL, rms: model.meters.rmsL, hold: model.meters.holdL, horizontal: true).frame(height: 4)
                    MeterBar(peak: model.meters.peakR, rms: model.meters.rmsR, hold: model.meters.holdR, horizontal: true).frame(height: 4)
                }
            }
        }
        .padding(12)
        .background(.background.secondary.opacity(0.8), in: RoundedRectangle(cornerRadius: 12))
    }
}

struct TaplineCommands: Commands {
    let model: AppModel

    var body: some Commands {
        CommandGroup(replacing: .appTermination) {
            Button("Hide to Menu Bar") { AppDelegate.hideToMenuBar() }
                .keyboardShortcut("q")
            Button("Quit Tapline") { NSApp.terminate(nil) }
                .keyboardShortcut("q", modifiers: [.command, .option])
        }
        CommandGroup(replacing: .undoRedo) {
            Button("Undo") { model.undo() }
                .keyboardShortcut("z")
                .disabled(!model.canUndo)
            Button("Redo") { model.redo() }
                .keyboardShortcut("z", modifiers: [.command, .shift])
                .disabled(!model.canRedo)
        }
        CommandMenu("Audio") {
            Button(model.powerOn ? "Turn Processing Off" : "Turn Processing On") { model.togglePower() }
                .keyboardShortcut("p", modifiers: [.command, .option])
            Button(model.settings.eqEnabled ? "Bypass EQ" : "Enable EQ") { model.setEQEnabled(!model.settings.eqEnabled) }
                .keyboardShortcut("b", modifiers: [.command])
            Button("Compare A/B") { model.toggleCompare() }
                .keyboardShortcut("k", modifiers: [.command])
            Button(model.simulating ? "Stop Listening As Another Device" : "Listen As Another Device") { model.toggleSimulation() }
                .disabled(model.simulation == nil)
            Divider()
            Button("Restart Audio Engine") { model.restartEngine() }
        }
    }
}
