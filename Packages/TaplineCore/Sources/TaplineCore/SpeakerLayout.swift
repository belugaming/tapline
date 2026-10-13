import Foundation

/// Where the speaker of one output channel stands, named as in Dolby's speaker layouts.
/// Read from Core Audio's channel labels on macOS and from the speaker mask on Windows.
public enum SpeakerPosition: String, Codable, CaseIterable, Sendable, Identifiable {
    case frontLeft, frontRight, center, lfe
    case frontLeftCenter, frontRightCenter, wideLeft, wideRight
    /// The surrounds beside the listener (5.1's surrounds, 7.1's side surrounds).
    case surroundLeft, surroundRight
    /// 7.1's rear surrounds, behind the listener.
    case rearLeft, rearRight, rearCenter
    case topFrontLeft, topFrontCenter, topFrontRight
    case topMiddleLeft, topMiddleCenter, topMiddleRight
    case topRearLeft, topRearCenter, topRearRight
    case lfe2
    /// A channel without a known place, such as an unlabelled channel of an interface.
    case unknown

    public var id: String { rawValue }

    public enum Group: Sendable, Equatable {
        case front, surround, height, lfe, unknown
    }

    public var group: Group {
        switch self {
        case .frontLeft, .frontRight, .center, .frontLeftCenter, .frontRightCenter, .wideLeft, .wideRight: .front
        case .surroundLeft, .surroundRight, .rearLeft, .rearRight, .rearCenter: .surround
        case .topFrontLeft, .topFrontCenter, .topFrontRight, .topMiddleLeft, .topMiddleCenter, .topMiddleRight,
             .topRearLeft, .topRearCenter, .topRearRight: .height
        case .lfe, .lfe2: .lfe
        case .unknown: .unknown
        }
    }

    public var isLFE: Bool { group == .lfe }
    public var isHeight: Bool { group == .height }
    /// The front left and right speakers, which the stereo effects act on.
    public var isFrontPair: Bool { self == .frontLeft || self == .frontRight }

    /// Dolby's short name: L, R, C, LFE, Ls, Rrs, Ltf…
    public var abbreviation: String {
        switch self {
        case .frontLeft: "L"
        case .frontRight: "R"
        case .center: "C"
        case .lfe: "LFE"
        case .frontLeftCenter: "Lc"
        case .frontRightCenter: "Rc"
        case .wideLeft: "Lw"
        case .wideRight: "Rw"
        case .surroundLeft: "Ls"
        case .surroundRight: "Rs"
        case .rearLeft: "Lrs"
        case .rearRight: "Rrs"
        case .rearCenter: "Cs"
        case .topFrontLeft: "Ltf"
        case .topFrontCenter: "Ctf"
        case .topFrontRight: "Rtf"
        case .topMiddleLeft: "Ltm"
        case .topMiddleCenter: "Ctm"
        case .topMiddleRight: "Rtm"
        case .topRearLeft: "Ltr"
        case .topRearCenter: "Ctr"
        case .topRearRight: "Rtr"
        case .lfe2: "LFE2"
        case .unknown: "?"
        }
    }

    /// Direction seen from the listening position in degrees, 0 straight ahead and positive
    /// to the right (Dolby's recommended angles), for drawing the layout.
    public var azimuth: Double {
        switch self {
        case .frontLeft: -30
        case .frontRight: 30
        case .center, .topFrontCenter, .topMiddleCenter: 0
        case .lfe: -15
        case .lfe2: 15
        case .frontLeftCenter: -15
        case .frontRightCenter: 15
        case .wideLeft: -60
        case .wideRight: 60
        case .surroundLeft: -100
        case .surroundRight: 100
        case .rearLeft: -145
        case .rearRight: 145
        case .rearCenter, .topRearCenter: 180
        case .topFrontLeft: -45
        case .topFrontRight: 45
        case .topMiddleLeft: -90
        case .topMiddleRight: 90
        case .topRearLeft: -135
        case .topRearRight: 135
        case .unknown: 0
        }
    }

    /// The position Core Audio means by an AudioChannelLabel, nil for labels that name no
    /// place (unused, unknown, discrete channels, ambisonics…).
    public init?(coreAudioLabel label: UInt32) {
        switch label {
        case 1, 38: self = .frontLeft                  // Left, LeftTotal
        case 2, 39: self = .frontRight                 // Right, RightTotal
        case 3, 42: self = .center                     // Center, Mono
        case 4: self = .lfe                            // LFEScreen
        case 5, 10: self = .surroundLeft               // LeftSurround, LeftSurroundDirect
        case 6, 11: self = .surroundRight              // RightSurround, RightSurroundDirect
        case 7: self = .frontLeftCenter
        case 8: self = .frontRightCenter
        case 9, 44: self = .rearCenter                 // CenterSurround, CenterSurroundDirect
        case 12: self = .topMiddleCenter               // TopCenterSurround
        case 13: self = .topFrontLeft                  // VerticalHeightLeft
        case 14: self = .topFrontCenter
        case 15: self = .topFrontRight
        case 16, 52: self = .topRearLeft               // TopBackLeft, LeftTopRear
        case 17, 53: self = .topRearCenter
        case 18, 54: self = .topRearRight
        case 33: self = .rearLeft                      // RearSurroundLeft
        case 34: self = .rearRight
        case 35: self = .wideLeft
        case 36: self = .wideRight
        case 37: self = .lfe2
        case 49: self = .topMiddleLeft                 // LeftTopMiddle
        case 51: self = .topMiddleRight
        default: return nil
        }
    }
}

/// The speakers of a multichannel output, one position per channel in channel order.
public struct SpeakerLayout: Codable, Hashable, Sendable {
    public var positions: [SpeakerPosition]

    public init(_ positions: [SpeakerPosition]) {
        self.positions = positions
    }

    public var channelCount: Int { positions.count }

    /// More than a left and right speaker: a centre, an LFE channel, surrounds or heights.
    /// Only such layouts get Tapline's multichannel processing; anything else is a stereo
    /// pair with extra channels that pass through untouched, as on an audio interface.
    public var isSurround: Bool {
        positions.contains { !$0.isFrontPair && $0 != .unknown }
    }

    /// "7.1.4": the speakers at ear height, the LFE channels and the height speakers.
    public var name: String {
        let ear = positions.filter { $0.group == .front || $0.group == .surround }.count
        let lfe = positions.filter(\.isLFE).count
        let top = positions.filter(\.isHeight).count
        return top > 0 ? "\(ear).\(lfe).\(top)" : "\(ear).\(lfe)"
    }

    /// The channels with a known speaker, in channel order: the ones the Room page measures.
    public var speakerChannels: [Int] {
        positions.indices.filter { positions[$0] != .unknown }
    }

    /// The timing reference of a measurement: the front left speaker, else the first known one.
    public var referenceChannel: Int? {
        positions.firstIndex(of: .frontLeft) ?? speakerChannels.first { !positions[$0].isLFE } ?? speakerChannels.first
    }

    // Core Audio's channel orders (kAudioChannelLayoutTag_MPEG_5_1_A, _AudioUnit_7_1, _Atmos_*).
    public static let surround51 = SpeakerLayout([.frontLeft, .frontRight, .center, .lfe, .surroundLeft, .surroundRight])
    public static let surround71 = SpeakerLayout([.frontLeft, .frontRight, .center, .lfe, .surroundLeft, .surroundRight, .rearLeft, .rearRight])
    public static let atmos512 = SpeakerLayout([.frontLeft, .frontRight, .center, .lfe, .surroundLeft, .surroundRight, .topMiddleLeft, .topMiddleRight])
    public static let atmos514 = SpeakerLayout([.frontLeft, .frontRight, .center, .lfe, .surroundLeft, .surroundRight,
                                                .topFrontLeft, .topFrontRight, .topRearLeft, .topRearRight])
    public static let atmos712 = SpeakerLayout([.frontLeft, .frontRight, .center, .lfe, .surroundLeft, .surroundRight, .rearLeft, .rearRight,
                                                .topMiddleLeft, .topMiddleRight])
    public static let atmos714 = SpeakerLayout([.frontLeft, .frontRight, .center, .lfe, .surroundLeft, .surroundRight, .rearLeft, .rearRight,
                                                .topFrontLeft, .topFrontRight, .topRearLeft, .topRearRight])
    public static let atmos916 = SpeakerLayout([.frontLeft, .frontRight, .center, .lfe, .surroundLeft, .surroundRight, .rearLeft, .rearRight,
                                                .wideLeft, .wideRight, .topFrontLeft, .topFrontRight, .topMiddleLeft, .topMiddleRight,
                                                .topRearLeft, .topRearRight])

    /// The usual layout for a channel count, for outputs that do not label their channels:
    /// 5.1 for 6 channels, 7.1 for 8, 5.1.4 for 10, 7.1.4 for 12, 9.1.6 for 16.
    public static func standard(channelCount: Int) -> SpeakerLayout? {
        switch channelCount {
        case 6: surround51
        case 8: surround71
        case 10: atmos514
        case 12: atmos714
        case 16: atmos916
        default: nil
        }
    }

    /// The layout of an output from its Core Audio channel labels (one per device channel; nil
    /// or an unmapped label where the device names none). When no channel beyond the front
    /// pair is labelled, an HDMI or DisplayPort output (an AV receiver or a TV) gets the
    /// standard layout for its channel count; any other output is a stereo pair.
    public static func fromCoreAudio(labels: [UInt32?], channelCount: Int, isHDMI: Bool) -> SpeakerLayout {
        var positions = (0..<channelCount).map { i -> SpeakerPosition in
            guard i < labels.count, let label = labels[i] else { return .unknown }
            return SpeakerPosition(coreAudioLabel: label) ?? .unknown
        }
        // A layout names each place once: a second channel with the same label is not that speaker.
        var seen = Set<SpeakerPosition>()
        for i in positions.indices where positions[i] != .unknown {
            if !seen.insert(positions[i]).inserted { positions[i] = .unknown }
        }
        let labelled = SpeakerLayout(positions)
        if labelled.isSurround { return labelled }
        if isHDMI, let standard = standard(channelCount: channelCount) { return standard }
        return labelled
    }
}
