import AudioToolbox
import CoreAudio
import Foundation

struct CoreAudioError: LocalizedError {
    let status: OSStatus
    let operation: String

    var errorDescription: String? { "\(operation) (\(fourCharCode(status)))" }
}

func fourCharCode(_ status: OSStatus) -> String {
    let n = UInt32(bitPattern: status)
    let bytes = [24, 16, 8, 0].map { UInt8((n >> $0) & 0xFF) }
    if bytes.allSatisfy({ $0 >= 32 && $0 < 127 }) {
        return "'" + String(decoding: bytes, as: UTF8.self) + "'"
    }
    return String(status)
}

/// Thin wrappers around the AudioObject property C API.
enum CA {
    static let system = AudioObjectID(kAudioObjectSystemObject)

    static func address(_ selector: AudioObjectPropertySelector,
                        _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal,
                        _ element: AudioObjectPropertyElement = kAudioObjectPropertyElementMain) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: element)
    }

    static func check(_ status: OSStatus, _ operation: @autoclosure () -> String) throws {
        if status != noErr { throw CoreAudioError(status: status, operation: operation()) }
    }

    static func has(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal, _ element: AudioObjectPropertyElement = kAudioObjectPropertyElementMain) -> Bool {
        var a = address(selector, scope, element)
        return AudioObjectHasProperty(id, &a)
    }

    static func isSettable(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal, _ element: AudioObjectPropertyElement = kAudioObjectPropertyElementMain) -> Bool {
        var a = address(selector, scope, element)
        var settable: DarwinBoolean = false
        guard AudioObjectHasProperty(id, &a), AudioObjectIsPropertySettable(id, &a, &settable) == noErr else { return false }
        return settable.boolValue
    }

    static func read<T>(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal, _ element: AudioObjectPropertyElement = kAudioObjectPropertyElementMain, default value: T) -> T {
        var a = address(selector, scope, element)
        guard AudioObjectHasProperty(id, &a) else { return value }
        var result = value
        var size = UInt32(MemoryLayout<T>.size)
        let status = withUnsafeMutablePointer(to: &result) {
            AudioObjectGetPropertyData(id, &a, 0, nil, &size, $0)
        }
        return status == noErr ? result : value
    }

    static func readArray<T>(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal, of _: T.Type) -> [T] {
        var a = address(selector, scope)
        var size: UInt32 = 0
        guard AudioObjectHasProperty(id, &a),
              AudioObjectGetPropertyDataSize(id, &a, 0, nil, &size) == noErr,
              size > 0 else { return [] }
        let count = Int(size) / MemoryLayout<T>.stride
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<T>.alignment)
        defer { buffer.deallocate() }
        guard AudioObjectGetPropertyData(id, &a, 0, nil, &size, buffer) == noErr else { return [] }
        let typed = buffer.bindMemory(to: T.self, capacity: count)
        return Array(UnsafeBufferPointer(start: typed, count: Int(size) / MemoryLayout<T>.stride))
    }

    static func readString(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> String? {
        var a = address(selector, scope)
        guard AudioObjectHasProperty(id, &a) else { return nil }
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        let status = withUnsafeMutablePointer(to: &value) {
            AudioObjectGetPropertyData(id, &a, 0, nil, &size, $0)
        }
        guard status == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }

    static func write<T>(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal, _ element: AudioObjectPropertyElement = kAudioObjectPropertyElementMain, value: T) throws {
        var a = address(selector, scope, element)
        var v = value
        let status = withUnsafeMutablePointer(to: &v) {
            AudioObjectSetPropertyData(id, &a, 0, nil, UInt32(MemoryLayout<T>.size), $0)
        }
        try check(status, "Set property \(fourCharCode(OSStatus(bitPattern: selector)))")
    }

    /// The channel labels (AudioChannelLabel) of a device's preferred output channel layout,
    /// one per channel in channel order; empty when it has none. Speaker setups made in Audio
    /// MIDI Setup (Configure Speakers) and HDMI receivers show up here.
    static func outputChannelLabels(_ id: AudioObjectID) -> [UInt32] {
        var a = address(kAudioDevicePropertyPreferredChannelLayout, kAudioObjectPropertyScopeOutput)
        var size: UInt32 = 0
        guard AudioObjectHasProperty(id, &a), AudioObjectGetPropertyDataSize(id, &a, 0, nil, &size) == noErr,
              Int(size) >= MemoryLayout<AudioChannelLayout>.size else { return [] }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioChannelLayout>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &a, 0, nil, &size, raw) == noErr else { return [] }
        return labels(of: UnsafePointer(raw.assumingMemoryBound(to: AudioChannelLayout.self)), size: Int(size))
    }

    /// The labels of a layout given by its descriptions, a layout tag or a channel bitmap.
    private static func labels(of layout: UnsafePointer<AudioChannelLayout>, size: Int, expand: Bool = true) -> [UInt32] {
        let tag = layout.pointee.mChannelLayoutTag
        if tag == kAudioChannelLayoutTag_UseChannelDescriptions {
            let count = Int(layout.pointee.mNumberChannelDescriptions)
            guard let offset = MemoryLayout<AudioChannelLayout>.offset(of: \.mChannelDescriptions),
                  offset + count * MemoryLayout<AudioChannelDescription>.stride <= size else { return [] }
            let descriptions = UnsafeRawPointer(layout).advanced(by: offset).assumingMemoryBound(to: AudioChannelDescription.self)
            return (0..<count).map { descriptions[$0].mChannelLabel }
        }
        guard expand else { return [] }
        // A tag or a bitmap: Audio Format Services spells it out as descriptions.
        let bitmap = tag == kAudioChannelLayoutTag_UseChannelBitmap
        var specifier: UInt32 = bitmap ? layout.pointee.mChannelBitmap.rawValue : tag
        let property = bitmap ? kAudioFormatProperty_ChannelLayoutForBitmap : kAudioFormatProperty_ChannelLayoutForTag
        var expandedSize: UInt32 = 0
        guard AudioFormatGetPropertyInfo(property, UInt32(MemoryLayout<UInt32>.size), &specifier, &expandedSize) == noErr,
              Int(expandedSize) >= MemoryLayout<AudioChannelLayout>.size else { return [] }
        let expanded = UnsafeMutableRawPointer.allocate(byteCount: Int(expandedSize), alignment: MemoryLayout<AudioChannelLayout>.alignment)
        defer { expanded.deallocate() }
        guard AudioFormatGetProperty(property, UInt32(MemoryLayout<UInt32>.size), &specifier, &expandedSize, expanded) == noErr else { return [] }
        return labels(of: UnsafePointer(expanded.assumingMemoryBound(to: AudioChannelLayout.self)), size: Int(expandedSize), expand: false)
    }

    /// Channel counts per stream buffer for one direction of a device.
    static func streamConfiguration(_ id: AudioObjectID, input: Bool) -> [Int] {
        var a = address(kAudioDevicePropertyStreamConfiguration, input ? kAudioObjectPropertyScopeInput : kAudioObjectPropertyScopeOutput)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &a, 0, nil, &size) == noErr, size > 0 else { return [] }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &a, 0, nil, &size, raw) == noErr else { return [] }
        let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return list.map { Int($0.mNumberChannels) }
    }
}

/// Registers an AudioObject property listener on the main queue and removes it on deinit.
final class PropertyListener {
    private let objectID: AudioObjectID
    private var addresses: [AudioObjectPropertyAddress]
    private let block: AudioObjectPropertyListenerBlock

    init(objectID: AudioObjectID, addresses: [AudioObjectPropertyAddress], handler: @escaping @MainActor () -> Void) {
        self.objectID = objectID
        self.addresses = addresses
        self.block = { @Sendable _, _ in
            MainActor.assumeIsolated { handler() }
        }
        for i in self.addresses.indices {
            AudioObjectAddPropertyListenerBlock(objectID, &self.addresses[i], DispatchQueue.main, block)
        }
    }

    convenience init(objectID: AudioObjectID, selectors: [AudioObjectPropertySelector], scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal, handler: @escaping @MainActor () -> Void) {
        self.init(objectID: objectID, addresses: selectors.map { CA.address($0, scope) }, handler: handler)
    }

    deinit {
        for i in addresses.indices {
            AudioObjectRemovePropertyListenerBlock(objectID, &addresses[i], DispatchQueue.main, block)
        }
    }
}
