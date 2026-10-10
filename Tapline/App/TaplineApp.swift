import AppKit
import SwiftUI

@main
struct TaplineApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = AppModel()
    @State private var autoEQ = AutoEQService()
    @State private var squig = SquigService()
    @State private var room = RoomMeasurementService()

    var body: some Scene {
        Window("Tapline", id: "main") {
            MainView()
                .environment(model)
                .environment(autoEQ)
                .environment(squig)
                .environment(room)
                .frame(minWidth: 980, minHeight: 640)
        }
        .defaultSize(width: 1180, height: 760)
        .windowResizability(.contentMinSize)
        .windowToolbarStyle(.unified(showsTitle: false))
        .commands { TaplineCommands(model: model) }

        MenuBarExtra {
            MenuBarPanel()
                .environment(model)
        } label: {
            MenuBarLabel(model: model)
        }
        .menuBarExtraStyle(.window)
    }
}

private struct MenuBarLabel: View {
    let model: AppModel

    var body: some View {
        Image(systemName: model.status == .running ? "slider.vertical.3" : "slider.horizontal.below.square.and.square.filled")
            .accessibilityLabel("Tapline")
    }
}
