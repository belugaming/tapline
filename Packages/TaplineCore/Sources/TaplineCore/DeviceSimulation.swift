import Foundation
import TaplineDSP

/// "Listen as": how the music would sound on another playback device, heard through the user's
/// own headphones. The device's curve plays in a filter block of its own after the EQ, so the
/// user's EQ and correction stay as they are, with the stereo width of the device's speakers and
/// a make-up gain that keeps the loudness when it is switched on or off.
///
/// Temporary: it is never part of `DSPSettings`, so presets, A/B and undo never see it.
public struct DeviceSimulation: Codable, Hashable, Sendable, Identifiable {
    public enum Kind: String, Codable, Sendable {
        /// Headphones or earbuds, fitted from a squig.link measurement.
        case measured
        /// One of the built-in speaker profiles: an approximation.
        case speaker
    }

    /// Filters the kernel's simulation block holds.
    public static let maxStages = Int(TL_MAX_SIM_STAGES)
    /// Filters of a fitted headphone simulation, shelves included.
    public static let fittedFilters = 12
    public static let gainRange: ClosedRange<Double> = -12...12

    /// The squig.link phone id, or the speaker profile's raw value.
    public var id: String
    public var name: String
    public var kind: Kind
    public var bands: [EQBand]
    /// 0 = mono, 1 = unchanged.
    public var width: Double
    /// Who measured a headphone (the squig.link database's site name).
    public var source: String?

    public init(id: String, name: String, kind: Kind, bands: [EQBand], width: Double = 1, source: String? = nil) {
        self.id = id
        self.name = name
        self.kind = kind
        // The kernel's block has a fixed number of stages: more would be dropped there.
        self.bands = Array(bands.prefix(Self.maxStages))
        self.width = width.isNaN ? 1 : width.clamped(to: 0...1)
        self.source = source
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.init(id: try c.decode(String.self, forKey: .id),
                  name: try c.decode(String.self, forKey: .name),
                  kind: try c.decode(Kind.self, forKey: .kind),
                  bands: try c.decodeIfPresent([EQBand].self, forKey: .bands) ?? [],
                  width: try c.decodeIfPresent(Double.self, forKey: .width) ?? 1,
                  source: try c.decodeIfPresent(String.self, forKey: .source))
    }

    /// The built-in profile this simulation plays, if it is one.
    public var profile: SpeakerProfile? { kind == .speaker ? SpeakerProfile(rawValue: id) : nil }

    private var playedBands: [EQBand] { bands.filter(\.isEnabled).map { $0.clamped() } }

    /// How much the device's curve changes the loudness of pink noise, in dB (LU, K-weighted
    /// like the EQ bypass). Earbuds that lose the bass come out quieter.
    public func loudnessChangeDB(sampleRate: Double = 48_000) -> Double {
        let played = playedBands
        guard !played.isEmpty else { return 0 }
        return ResponseCurve.loudnessChangeDB(bands: played, sampleRate: sampleRate)
    }

    /// Gain before the device's filters that cancels their loudness change, so that switching
    /// the simulation compares sound, not level. Mono and narrow stereo leave a centred sound
    /// as loud as it was, so the width does not count.
    public func makeupGainDB(sampleRate: Double = 48_000) -> Double {
        let gain = -loudnessChangeDB(sampleRate: sampleRate)
        return gain.isFinite ? gain.clamped(to: Self.gainRange) : 0
    }

    /// The make-up gain for a chain without a limiter behind it (the limiter switched off, or
    /// Equalizer APO): lowered where the simulation would raise some frequency above the level
    /// `settings`' EQ reaches by itself (0 dB with the automatic preamp), so it cannot clip what
    /// the EQ does not. Switching is then less than level-matched, by `levelShortfallDB`.
    public func makeupGainDB(after settings: DSPSettings, sampleRate: Double = 48_000, levelMatchedBypass: Bool = true) -> Double {
        let gain = makeupGainDB(sampleRate: sampleRate)
        let own = playedBands
        guard !own.isEmpty else { return gain }
        let eq = settings.eqEnabled ? settings.activeBands.map { $0.clamped() } : []
        let base = settings.eqEnabled ? settings.effectivePreampDB(sampleRate: sampleRate)
            : (levelMatchedBypass ? settings.bypassGainDB(sampleRate: sampleRate) : 0)
        let channels: [EQChannels?] = settings.eqEnabled && settings.hasChannelSpecificBands ? [.left, .right] : [nil]
        let device = ResponseCurve.responseDB(bands: own, frequencies: Self.probe, sampleRate: sampleRate)
        var allowed = gain
        for channel in channels {
            let before = ResponseCurve.responseDB(bands: eq, frequencies: Self.probe, sampleRate: sampleRate, channel: channel).map { $0 + base }
            let ceiling = max(0, before.max() ?? 0)
            let peak = zip(before, device).map { $0 + $1 }.max() ?? 0
            allowed = min(allowed, ceiling - peak)
        }
        return allowed.isFinite ? allowed.clamped(to: Self.gainRange) : 0
    }

    /// How much quieter than level-matched the simulation plays with `makeupGainDB(after:)`, in dB.
    public func levelShortfallDB(after settings: DSPSettings, sampleRate: Double = 48_000, levelMatchedBypass: Bool = true) -> Double {
        max(0, makeupGainDB(sampleRate: sampleRate) - makeupGainDB(after: settings, sampleRate: sampleRate, levelMatchedBypass: levelMatchedBypass))
    }

    private static let probe = ResponseCurve.logFrequencies(count: 480)

    /// The kernel's simulation block. `enabled` false keeps the filters in place, so switching
    /// off fades out like switching on fades in.
    func kernelBlock(sampleRate: Double, makeupDB: Double, enabled: Bool) -> tl_simulation {
        var block = tl_simulation()
        let neutral = tl_biquad(b0: 1, b1: 0, b2: 0, a1: 0, a2: 0, active: 0, channels: 0)
        var stages = [tl_biquad](repeating: neutral, count: Self.maxStages)
        for (i, band) in bands.prefix(Self.maxStages).enumerated() where band.isEnabled {
            stages[i] = BiquadCoefficients(band: band.clamped(), sampleRate: sampleRate).cValue()
        }
        withUnsafeMutableBytes(of: &block.stages) { raw in
            let ptr = raw.bindMemory(to: tl_biquad.self)
            for i in 0..<Self.maxStages { ptr[i] = stages[i] }
        }
        block.gain = Float(dbToLinear(makeupDB.isFinite ? makeupDB.clamped(to: Self.gainRange) : 0))
        block.width = Float(width.clamped(to: 0...1))
        block.enabled = enabled ? 1 : 0
        return block
    }
}

// MARK: - Speaker profiles

/// Built-in approximations of the speakers in everyday devices. They are written from general,
/// published knowledge of how such speakers behave (small drivers cannot move enough air for
/// bass, small boxes resonate, TV speakers fire downwards, a car cabin lifts the bass), not from
/// measurements of any product: they show a typical character, not one model.
public enum SpeakerProfile: String, CaseIterable, Codable, Sendable, Identifiable {
    case phone, laptop, tv, bluetoothSpeaker, car

    public var id: String { rawValue }

    /// English name; the apps show their own translations.
    public var name: String {
        switch self {
        case .phone: "Phone speaker"
        case .laptop: "Laptop speakers"
        case .tv: "TV speakers"
        case .bluetoothSpeaker: "Bluetooth speaker"
        case .car: "Car"
        }
    }

    /// Stereo width of the speakers as heard from where one sits: 0 = mono.
    public var width: Double {
        switch self {
        case .phone: 0            // one speaker, or two a few centimetres apart
        case .laptop: 0.35        // two drivers a hand's width apart, close to the ears
        case .tv: 0.6
        case .bluetoothSpeaker: 0 // one box
        case .car: 0.7            // off-centre seat: the near side dominates
        }
    }

    public var bands: [EQBand] {
        switch self {
        case .phone:
            // A micro speaker in a tiny chamber: nothing below its resonance around 800 Hz,
            // a honky hump above it, forward presence, and a soft top.
            [
                EQBand(type: .highPass, frequency: 750, q: 1.0),
                EQBand(type: .highPass, frequency: 400, q: 0.7),
                EQBand(type: .peak, frequency: 1100, gain: 2, q: 1.4),
                EQBand(type: .peak, frequency: 3500, gain: 4, q: 1.6),
                EQBand(type: .peak, frequency: 6500, gain: -3, q: 2.5),
                EQBand(type: .lowPass, frequency: 12_000, q: 0.8),
            ]
        case .laptop:
            // Small drivers in the chassis: bass only from about 250 Hz, a boxy low midrange,
            // an uneven upper midrange, little air through the grilles.
            [
                EQBand(type: .highPass, frequency: 280, q: 1.2),
                EQBand(type: .highPass, frequency: 160, q: 0.7),
                EQBand(type: .peak, frequency: 450, gain: 2, q: 1.5),
                EQBand(type: .peak, frequency: 1800, gain: 2, q: 1.5),
                EQBand(type: .peak, frequency: 4500, gain: 2.5, q: 2),
                EQBand(type: .highShelf, frequency: 9000, gain: -4, q: 0.7),
            ]
        case .tv:
            // Slim drivers firing down or backwards: a bass bump from the processing above a
            // cut-off near 100 Hz, some boxiness, dialogue lifted, the treble lost to the table.
            [
                EQBand(type: .highPass, frequency: 110, q: 1.1),
                EQBand(type: .highPass, frequency: 75, q: 0.7),
                EQBand(type: .peak, frequency: 160, gain: 2, q: 1.4),
                EQBand(type: .peak, frequency: 300, gain: 1.5, q: 1.2),
                EQBand(type: .peak, frequency: 2500, gain: 1.5, q: 1),
                EQBand(type: .highShelf, frequency: 5000, gain: -5, q: 0.7),
                EQBand(type: .lowPass, frequency: 15_000, q: 0.7),
            ]
        case .bluetoothSpeaker:
            // One small box with passive radiators: a strong bass bump just above a steep
            // cut-off, scooped mids, crisp treble that fades early.
            [
                EQBand(type: .highPass, frequency: 75, q: 1.3),
                EQBand(type: .highPass, frequency: 60, q: 0.7),
                EQBand(type: .peak, frequency: 110, gain: 4, q: 1.3),
                EQBand(type: .peak, frequency: 1200, gain: -2, q: 0.9),
                EQBand(type: .peak, frequency: 5000, gain: 2.5, q: 1.6),
                EQBand(type: .lowPass, frequency: 14_000, q: 0.7),
            ]
        case .car:
            // The closed cabin lifts the deep bass, door speakers and reflections leave a dip
            // in the upper bass and a peak from the windscreen, off-axis tweeters lose treble.
            [
                EQBand(type: .highPass, frequency: 28, q: 0.7),
                EQBand(type: .lowShelf, frequency: 70, gain: 6, q: 0.7),
                EQBand(type: .peak, frequency: 250, gain: -3, q: 1.4),
                EQBand(type: .peak, frequency: 2800, gain: 2, q: 2),
                EQBand(type: .highShelf, frequency: 7000, gain: -4, q: 0.7),
            ]
        }
    }

    public var simulation: DeviceSimulation {
        DeviceSimulation(id: rawValue, name: name, kind: .speaker, bands: bands, width: width)
    }
}

// MARK: - Headphones and earbuds

/// What a headphone correction was fitted to, so "Listen as" can compare another device with
/// the sound the correction aims at. Recorded when a squig.link or own-measurement correction
/// is applied.
public struct CorrectionReference: Codable, Hashable, Sendable {
    /// squig.link database (its id) the headphones were measured in; nil for the user's own measurement.
    public var measurementDatabase: String?
    /// Database the target came from (the measurement's own, or a fallback), and its name.
    public var targetDatabase: String
    public var targetSite: String
    public var targetName: String

    public init(measurementDatabase: String?, targetDatabase: String, targetSite: String, targetName: String) {
        self.measurementDatabase = measurementDatabase
        self.targetDatabase = targetDatabase
        self.targetSite = targetSite
        self.targetName = targetName
    }
}

extension DeviceSimulation {
    /// How a device's measurement is compared with the user's sound.
    public enum Comparison: String, Codable, Sendable {
        /// Both come from the same database, so the same rig: the raw difference between the
        /// device and the target the correction aims at.
        case sameRig
        /// Different databases: each against its own database's target, so that what the rigs
        /// add (coupler, ear simulator, calibration) cancels.
        case crossRig
    }

    /// Which curve stands for "the user's sound" when simulating a device.
    public struct Plan: Hashable, Sendable {
        public var comparison: Comparison
        /// Database id and name of the reference target.
        public var referenceDatabase: String
        public var referenceTarget: String
    }

    /// Same rig when the device was measured in the database the user's headphones were.
    public static func comparison(deviceDatabase: String, correction: CorrectionReference?) -> Comparison {
        correction?.measurementDatabase == deviceDatabase ? .sameRig : .crossRig
    }

    /// The reference for a device measured in `deviceDatabase`, among the `targets` of
    /// `targetDatabase`: on a same-rig comparison the database the correction's target came
    /// from, else the device's own database (or its fallback when it has no targets).
    /// `preferredTarget` if the database has it, else the one named like the user's target (on
    /// the same rig: the very target the correction aims at), else a Harman target, else the
    /// first. Nil when no target is available.
    public static func plan(deviceDatabase: String, targetDatabase: String, targets: [String],
                            correction: CorrectionReference?, preferredTarget: String? = nil) -> Plan? {
        guard let name = preferredTarget.flatMap({ targets.contains($0) ? $0 : nil })
                ?? referenceTarget(among: targets, userTarget: correction?.targetName) else { return nil }
        return Plan(comparison: comparison(deviceDatabase: deviceDatabase, correction: correction),
                    referenceDatabase: targetDatabase, referenceTarget: name)
    }

    /// The target of a database that best matches the user's target: the same name, else a
    /// Harman target, else the first.
    public static func referenceTarget(among targets: [String], userTarget: String?) -> String? {
        if let userTarget, let same = targets.first(where: { $0.caseInsensitiveCompare(userTarget) == .orderedSame }) { return same }
        return targets.first { $0.localizedCaseInsensitiveContains("harman") } ?? targets.first
    }

    /// Fits the EQ that turns `reference` (what the user hears now: the target their correction
    /// aims at, or the device's own database's target) into `device`, with the fitter acting on
    /// the device as its target. The fitter's treble handling applies: above ~10 kHz measurements
    /// differ from rig to rig and fit to fit, so only the broad trend is copied.
    public static func fit(device: FrequencyResponse, reference: FrequencyResponse, filters: Int = fittedFilters) -> EQFitter.Result {
        var options = EQFitter.Options(maxFilters: min(filters, maxStages))
        // Devices differ from a target far more than headphones need correcting.
        options.maxBoostDB = 10
        options.maxCutDB = 18
        return EQFitter.fit(measurement: reference, target: device, options: options)
    }
}
