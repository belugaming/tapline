import AudioToolbox
import CoreAudio
import Foundation
import Observation
import TaplineCore

struct AudioDevice: Identifiable, Hashable, Sendable {
    let id: AudioObjectID
    let uid: String
    let name: String
    let manufacturer: String
    let transportType: UInt32
    let outputChannels: Int
    let inputChannels: Int
    let inputStreamCount: Int
    let sampleRate: Double
    let availableSampleRates: [Double]
    let preferredStereo: [UInt32]
    let canBeDefaultOutput: Bool
    let canBeDefaultInput: Bool
    let hasOutputVolume: Bool

    var isOutput: Bool { outputChannels > 0 }
    var isBluetooth: Bool { transportType == kAudioDeviceTransportTypeBluetooth || transportType == kAudioDeviceTransportTypeBluetoothLE }
    var isVirtual: Bool { transportType == kAudioDeviceTransportTypeVirtual }

    /// The properties that decide how the engine is wired. Anything else (name,
    /// volume support...) can change without rebuilding the audio path.
    var routingSignature: String {
        "\(id)|\(uid)|\(sampleRate)|\(outputChannels)|\(inputStreamCount)|\(preferredStereo)"
    }
    var isInput: Bool { inputChannels > 0 }

    var transportName: String {
        switch transportType {
        case kAudioDeviceTransportTypeBuiltIn: String(localized: "Built-in")
        case kAudioDeviceTransportTypeUSB: "USB"
        case kAudioDeviceTransportTypeBluetooth, kAudioDeviceTransportTypeBluetoothLE: String(localized: "Bluetooth")
        case kAudioDeviceTransportTypeAirPlay: "AirPlay"
        case kAudioDeviceTransportTypeHDMI: "HDMI"
        case kAudioDeviceTransportTypeDisplayPort: "DisplayPort"
        case kAudioDeviceTransportTypeThunderbolt: "Thunderbolt"
        case kAudioDeviceTransportTypeFireWire: "FireWire"
        case kAudioDeviceTransportTypeAggregate: String(localized: "Aggregate")
        case kAudioDeviceTransportTypeVirtual: String(localized: "Virtual")
        case kAudioDeviceTransportTypeContinuityCaptureWired, kAudioDeviceTransportTypeContinuityCaptureWireless: "Continuity"
        default: String(localized: "Other")
        }
    }

    var symbolName: String {
        switch transportType {
        case kAudioDeviceTransportTypeBuiltIn: isOutput ? "laptopcomputer" : "mic"
        case kAudioDeviceTransportTypeBluetooth, kAudioDeviceTransportTypeBluetoothLE: "headphones"
        case kAudioDeviceTransportTypeAirPlay: "airplayaudio"
        case kAudioDeviceTransportTypeHDMI, kAudioDeviceTransportTypeDisplayPort: "tv"
        case kAudioDeviceTransportTypeUSB, kAudioDeviceTransportTypeThunderbolt, kAudioDeviceTransportTypeFireWire: "hifispeaker"
        case kAudioDeviceTransportTypeVirtual, kAudioDeviceTransportTypeAggregate: "square.stack.3d.up"
        case kAudioDeviceTransportTypeContinuityCaptureWired, kAudioDeviceTransportTypeContinuityCaptureWireless: "iphone"
        default: "speaker.wave.2"
        }
    }

    static func load(_ id: AudioObjectID) -> AudioDevice? {
        guard let uid = CA.readString(id, kAudioDevicePropertyDeviceUID) else { return nil }
        let outputs = CA.streamConfiguration(id, input: false)
        let inputs = CA.streamConfiguration(id, input: true)
        let rates = CA.readArray(id, kAudioDevicePropertyAvailableNominalSampleRates, of: AudioValueRange.self)
            .flatMap { r -> [Double] in
                if r.mMinimum == r.mMaximum { return [r.mMinimum] }
                return [44_100, 48_000, 88_200, 96_000, 176_400, 192_000].filter { $0 >= r.mMinimum && $0 <= r.mMaximum }
            }
        let stereo = CA.readArray(id, kAudioDevicePropertyPreferredChannelsForStereo, kAudioObjectPropertyScopeOutput, of: UInt32.self)
        return AudioDevice(
            id: id,
            uid: uid,
            name: CA.readString(id, kAudioObjectPropertyName) ?? uid,
            manufacturer: CA.readString(id, kAudioObjectPropertyManufacturer) ?? "",
            transportType: CA.read(id, kAudioDevicePropertyTransportType, default: UInt32(0)),
            outputChannels: outputs.reduce(0, +),
            inputChannels: inputs.reduce(0, +),
            inputStreamCount: inputs.count,
            sampleRate: CA.read(id, kAudioDevicePropertyNominalSampleRate, default: 0.0),
            availableSampleRates: Array(Set(rates)).sorted(),
            preferredStereo: stereo,
            canBeDefaultOutput: CA.read(id, kAudioDevicePropertyDeviceCanBeDefaultDevice, kAudioObjectPropertyScopeOutput, default: UInt32(0)) != 0,
            canBeDefaultInput: CA.read(id, kAudioDevicePropertyDeviceCanBeDefaultDevice, kAudioObjectPropertyScopeInput, default: UInt32(0)) != 0,
            hasOutputVolume: CA.has(id, kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioObjectPropertyScopeOutput)
        )
    }
}

/// Live list of audio devices and the system defaults.
@MainActor
@Observable
final class AudioDeviceCenter {
    private(set) var devices: [AudioDevice] = []
    private(set) var defaultOutputID: AudioObjectID = 0
    private(set) var defaultInputID: AudioObjectID = 0
    private(set) var systemOutputID: AudioObjectID = 0
    /// Bumped whenever something about the default output changes (sample rate, streams).
    private(set) var outputRevision = 0
    private(set) var outputVolume: Float = 0
    private(set) var outputMuted = false

    var outputDevices: [AudioDevice] { devices.filter { $0.isOutput && $0.canBeDefaultOutput } }
    var inputDevices: [AudioDevice] { devices.filter { $0.isInput && $0.canBeDefaultInput } }
    var defaultOutput: AudioDevice? { devices.first { $0.id == defaultOutputID } }
    var defaultInput: AudioDevice? { devices.first { $0.id == defaultInputID } }

    /// Called after the device list, a default device or the default output's format changed.
    @ObservationIgnored var onChange: (@MainActor () -> Void)?

    @ObservationIgnored private var systemListener: PropertyListener?
    @ObservationIgnored private var outputListener: PropertyListener?
    @ObservationIgnored private var volumeListener: PropertyListener?
    @ObservationIgnored private var listenedOutputID: AudioObjectID = 0

    static let privateAggregatePrefix = "io.github.belugaming.tapline.aggregate"

    init() {
        refresh()
        systemListener = PropertyListener(objectID: CA.system, selectors: [
            kAudioHardwarePropertyDevices,
            kAudioHardwarePropertyDefaultOutputDevice,
            kAudioHardwarePropertyDefaultInputDevice,
            kAudioHardwarePropertyDefaultSystemOutputDevice,
        ]) { [weak self] in
            self?.refresh()
        }
    }

    @ObservationIgnored private var demoMode = false

    /// Fixed, anonymous devices for README screenshots (snapshot mode only).
    func applyDemo() {
        demoMode = true
        devices = [
            AudioDevice(id: 9101, uid: "demo.airpods", name: "AirPods Pro", manufacturer: "Apple Inc.", transportType: kAudioDeviceTransportTypeBluetooth,
                        outputChannels: 2, inputChannels: 0, inputStreamCount: 0, sampleRate: 48_000, availableSampleRates: [48_000],
                        preferredStereo: [1, 2], canBeDefaultOutput: true, canBeDefaultInput: false, hasOutputVolume: true),
            AudioDevice(id: 9102, uid: "demo.interface", name: "USB Audio Interface", manufacturer: "Studio", transportType: kAudioDeviceTransportTypeUSB,
                        outputChannels: 10, inputChannels: 10, inputStreamCount: 1, sampleRate: 48_000, availableSampleRates: [44_100, 48_000, 88_200, 96_000],
                        preferredStereo: [1, 2], canBeDefaultOutput: true, canBeDefaultInput: true, hasOutputVolume: false),
            AudioDevice(id: 9103, uid: "demo.speakers", name: "MacBook Pro Speakers", manufacturer: "Apple Inc.", transportType: kAudioDeviceTransportTypeBuiltIn,
                        outputChannels: 2, inputChannels: 0, inputStreamCount: 0, sampleRate: 48_000, availableSampleRates: [44_100, 48_000],
                        preferredStereo: [1, 2], canBeDefaultOutput: true, canBeDefaultInput: false, hasOutputVolume: true),
            AudioDevice(id: 9104, uid: "demo.mic", name: "MacBook Pro Microphone", manufacturer: "Apple Inc.", transportType: kAudioDeviceTransportTypeBuiltIn,
                        outputChannels: 0, inputChannels: 1, inputStreamCount: 1, sampleRate: 48_000, availableSampleRates: [48_000],
                        preferredStereo: [], canBeDefaultOutput: false, canBeDefaultInput: true, hasOutputVolume: false),
        ]
        defaultOutputID = 9101
        defaultInputID = 9104
        systemOutputID = 9101
        outputVolume = 0.55
        outputMuted = false
    }

    func refresh() {
        guard !demoMode else { return }
        let ids = CA.readArray(CA.system, kAudioHardwarePropertyDevices, of: AudioObjectID.self)
        devices = ids.compactMap(AudioDevice.load)
            .filter { !$0.uid.hasPrefix(Self.privateAggregatePrefix) && !$0.uid.hasPrefix("CADefaultDeviceAggregate") }
        defaultOutputID = CA.read(CA.system, kAudioHardwarePropertyDefaultOutputDevice, default: AudioObjectID(0))
        defaultInputID = CA.read(CA.system, kAudioHardwarePropertyDefaultInputDevice, default: AudioObjectID(0))
        systemOutputID = CA.read(CA.system, kAudioHardwarePropertyDefaultSystemOutputDevice, default: AudioObjectID(0))
        watchDefaultOutput()
        readVolume()
        onChange?()
    }

    private func watchDefaultOutput() {
        guard listenedOutputID != defaultOutputID else { return }
        listenedOutputID = defaultOutputID
        outputListener = PropertyListener(objectID: defaultOutputID, addresses: [
            CA.address(kAudioDevicePropertyNominalSampleRate),
            CA.address(kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeOutput),
            CA.address(kAudioDevicePropertyPreferredChannelsForStereo, kAudioObjectPropertyScopeOutput),
        ]) { [weak self] in
            guard let self, !self.demoMode else { return }
            self.devices = CA.readArray(CA.system, kAudioHardwarePropertyDevices, of: AudioObjectID.self)
                .compactMap(AudioDevice.load)
                .filter { !$0.uid.hasPrefix(Self.privateAggregatePrefix) && !$0.uid.hasPrefix("CADefaultDeviceAggregate") }
            self.outputRevision += 1
            self.onChange?()
        }
        volumeListener = PropertyListener(objectID: defaultOutputID, addresses: [
            CA.address(kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioObjectPropertyScopeOutput),
            CA.address(kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput),
        ]) { [weak self] in
            self?.readVolume()
        }
    }

    private func readVolume() {
        guard !demoMode else { return }
        outputVolume = CA.read(defaultOutputID, kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioObjectPropertyScopeOutput, default: Float(0))
        outputMuted = CA.read(defaultOutputID, kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput, default: UInt32(0)) != 0
    }

    func setDefaultOutput(_ device: AudioDevice) {
        try? CA.write(CA.system, kAudioHardwarePropertyDefaultOutputDevice, value: device.id)
        if systemOutputID == defaultOutputID {
            // Keep alert sounds following the main output, like the Sound settings pane does.
            try? CA.write(CA.system, kAudioHardwarePropertyDefaultSystemOutputDevice, value: device.id)
        }
        refresh()
    }

    func setDefaultInput(_ device: AudioDevice) {
        try? CA.write(CA.system, kAudioHardwarePropertyDefaultInputDevice, value: device.id)
        refresh()
    }

    func setSampleRate(_ rate: Double, for device: AudioDevice) {
        try? CA.write(device.id, kAudioDevicePropertyNominalSampleRate, value: rate)
        refresh()
    }

    func setOutputVolume(_ value: Float) {
        try? CA.write(defaultOutputID, kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioObjectPropertyScopeOutput, value: value.clamped(to: 0...1))
        outputVolume = value
    }

    func setOutputMuted(_ muted: Bool) {
        try? CA.write(defaultOutputID, kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput, value: UInt32(muted ? 1 : 0))
        outputMuted = muted
    }
}
