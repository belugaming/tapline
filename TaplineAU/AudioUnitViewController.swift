import CoreAudioKit
import SwiftUI

/// The extension's principal class: makes the Audio Unit for the host and shows its view.
public final class AudioUnitViewController: AUViewController, AUAudioUnitFactory {
    private var audioUnit: TaplineAudioUnit?
    private var hostingView: NSHostingView<AudioUnitView>?

    public override func loadView() {
        view = NSView(frame: CGRect(origin: .zero, size: AudioUnitView.idealSize))
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        preferredContentSize = AudioUnitView.idealSize
        if let audioUnit { show(audioUnit) }
    }

    nonisolated public func createAudioUnit(with componentDescription: AudioComponentDescription) throws -> AUAudioUnit {
        let unit = try TaplineAudioUnit(componentDescription: componentDescription, options: [])
        let created = UncheckedUnit(unit: unit)
        DispatchQueue.main.async {
            MainActor.assumeIsolated {
                self.audioUnit = created.unit
                if self.isViewLoaded { self.show(created.unit) }
            }
        }
        return unit
    }

    private func show(_ unit: TaplineAudioUnit) {
        guard hostingView == nil else { return }
        let model = AudioUnitModel(engine: unit.engine) { [weak unit] parameter, value, gesture in
            unit?.setParameter(parameter, value, gesture: gesture)
        }
        let hosting = NSHostingView(rootView: AudioUnitView(model: model))
        hosting.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(hosting)
        NSLayoutConstraint.activate([
            hosting.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            hosting.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            hosting.topAnchor.constraint(equalTo: view.topAnchor),
            hosting.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        hostingView = hosting
    }
}

/// The Audio Unit handed from the extension's thread to the main thread.
private struct UncheckedUnit: @unchecked Sendable {
    let unit: TaplineAudioUnit
}
