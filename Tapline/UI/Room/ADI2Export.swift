import AppKit
import TaplineCore
import UniformTypeIdentifiers

/// Saving RME ADI-2 presets, shared by the room page and Import & Export.
enum ADI2Export {
    static let contentType = UTType(filenameExtension: ADI2Preset.fileExtension) ?? .xml

    /// Asks where to save and writes the file. Returns false when cancelled or failed.
    @MainActor
    static func save(_ text: String, suggestedName: String) throws -> Bool {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [contentType]
        panel.nameFieldStringValue = "\(suggestedName).\(ADI2Preset.fileExtension)"
        guard panel.runModal() == .OK, let url = panel.url else { return false }
        try text.write(to: url, atomically: true, encoding: .utf8)
        return true
    }

    static func message(for error: Error) -> String {
        switch error as? ADI2Preset.Error {
        case .doesNotFit:
            String(localized: "The ADI-2 has five bells per channel, and bands 4 and 5 only work from 200 Hz up with Q up to 5. Use fewer bands, or fit the room correction for the ADI-2.")
        case .unsupportedFilter:
            String(localized: "The ADI-2 takes bells, one low shelf (as Bass, 20–150 Hz) and one high shelf (as Treble, 3–10 kHz). Change or remove the other filters.")
        case .notAPreset:
            String(localized: "That is not an ADI-2 EQ preset.")
        case nil:
            error.localizedDescription
        }
    }

    static let loadHint: LocalizedStringResource = "In ADI-2 Remote, open EQ Presets, click File – Load and pick the file, then Store it to the unit."
}
