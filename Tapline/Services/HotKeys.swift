import AppKit
import Carbon.HIToolbox

/// System-wide keyboard shortcuts (Carbon hot keys: no Accessibility permission needed).
@MainActor
final class HotKeyCenter {
    struct Shortcut: Identifiable {
        let id: UInt32
        let keyCode: UInt32
        let title: LocalizedStringResource
        let action: @MainActor () -> Void

        /// ⌃⌥⌘ + key, rarely used by other apps.
        static let modifiers = UInt32(controlKey | optionKey | cmdKey)

        var display: String {
            let key: String
            switch Int(keyCode) {
            case kVK_ANSI_P: key = "P"
            case kVK_ANSI_B: key = "B"
            case kVK_ANSI_K: key = "K"
            case kVK_ANSI_L: key = "L"
            default: key = "?"
            }
            return "⌃⌥⌘\(key)"
        }
    }

    static let shared = HotKeyCenter()

    private(set) var shortcuts: [Shortcut] = []
    private var refs: [EventHotKeyRef] = []
    private var handlerInstalled = false

    func register(_ list: [Shortcut]) {
        unregisterAll()
        installHandlerIfNeeded()
        shortcuts = list
        for s in list {
            var ref: EventHotKeyRef?
            let id = EventHotKeyID(signature: hotKeySignature, id: s.id)
            if RegisterEventHotKey(s.keyCode, Shortcut.modifiers, id, GetApplicationEventTarget(), 0, &ref) == noErr, let ref {
                refs.append(ref)
            }
        }
    }

    func unregisterAll() {
        for ref in refs { UnregisterEventHotKey(ref) }
        refs = []
        shortcuts = []
    }

    fileprivate func fire(_ id: UInt32) {
        shortcuts.first { $0.id == id }?.action()
    }

    private func installHandlerIfNeeded() {
        guard !handlerInstalled else { return }
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), hotKeyHandler, 1, &spec, nil, nil)
        handlerInstalled = true
    }
}

private let hotKeySignature: OSType = 0x544C_4E45 // 'TLNE'

private func hotKeyHandler(_ next: EventHandlerCallRef?, _ event: EventRef?, _ userData: UnsafeMutableRawPointer?) -> OSStatus {
    guard let event else { return OSStatus(eventNotHandledErr) }
    var id = EventHotKeyID()
    let status = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                   nil, MemoryLayout<EventHotKeyID>.size, nil, &id)
    guard status == noErr, id.signature == hotKeySignature else { return OSStatus(eventNotHandledErr) }
    let key = id.id
    DispatchQueue.main.async {
        MainActor.assumeIsolated { HotKeyCenter.shared.fire(key) }
    }
    return noErr
}
