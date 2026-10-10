import CoreAudio
import Foundation
import TaplineDSP

public struct KernelMeters: Sendable, Equatable {
    public var peakL: Float = 0
    public var peakR: Float = 0
    public var rmsL: Float = 0
    public var rmsR: Float = 0
    public var gainReductionDB: Float = 0
    public var clippedSamples: UInt32 = 0
    public var ioCycles: UInt64 = 0
    public var sourcePeaks: [Float] = []

    public init() {}
}

public struct KernelLayout: Sendable, Equatable {
    public var sourceBufferIndices: [Int]
    /// Stereo pair inside each source buffer (0/1 for mixdown taps).
    public var sourceLeftChannel: Int
    public var sourceRightChannel: Int
    public var leftBuffer: Int
    public var leftChannel: Int
    public var rightBuffer: Int
    public var rightChannel: Int
    /// Sources mirror the output stream; pass non-stereo channels through.
    public var passthrough: Bool

    public init(sourceBufferIndices: [Int], sourceLeftChannel: Int = 0, sourceRightChannel: Int = 1,
                leftBuffer: Int, leftChannel: Int, rightBuffer: Int, rightChannel: Int, passthrough: Bool = false) {
        self.sourceBufferIndices = sourceBufferIndices
        self.sourceLeftChannel = sourceLeftChannel
        self.sourceRightChannel = sourceRightChannel
        self.leftBuffer = leftBuffer
        self.leftChannel = leftChannel
        self.rightBuffer = rightBuffer
        self.rightChannel = rightChannel
        self.passthrough = passthrough
    }
}

/// Owns the C realtime kernel. All methods are safe to call from the main thread.
public final class DSPKernel: @unchecked Sendable {
    public static let maxSources = Int(TL_MAX_SOURCES)
    public static let analyzerCapacity = Int(TL_ANALYZER_SIZE)

    public let pointer: OpaquePointer

    public init() {
        guard let k = tl_kernel_create() else { fatalError("Unable to allocate DSP kernel") }
        pointer = k
    }

    deinit {
        tl_kernel_destroy(pointer)
    }

    /// Opaque client-data pointer for the IOProc.
    public var clientData: UnsafeMutableRawPointer { UnsafeMutableRawPointer(pointer) }

    /// The C IOProc to register with AudioDeviceCreateIOProcID.
    public static let ioProc: AudioDeviceIOProc = tl_ioproc

    public func configure(sampleRate: Double, layout: KernelLayout) {
        var l = tl_layout()
        l.source_count = Int32(min(layout.sourceBufferIndices.count, Self.maxSources))
        withUnsafeMutableBytes(of: &l.source_buffer_index) { raw in
            let p = raw.bindMemory(to: Int32.self)
            for i in 0..<Self.maxSources {
                p[i] = i < layout.sourceBufferIndices.count ? Int32(layout.sourceBufferIndices[i]) : -1
            }
        }
        l.source_channel_l = Int32(layout.sourceLeftChannel)
        l.source_channel_r = Int32(layout.sourceRightChannel)
        l.passthrough = layout.passthrough ? 1 : 0
        l.out_buffer_l = Int32(layout.leftBuffer)
        l.out_channel_l = Int32(layout.leftChannel)
        l.out_buffer_r = Int32(layout.rightBuffer)
        l.out_channel_r = Int32(layout.rightChannel)
        tl_kernel_configure(pointer, sampleRate, &l)
    }

    public func setSettings(_ settings: DSPSettings, sampleRate: Double) {
        var p = settings.kernelParams(sampleRate: sampleRate)
        tl_kernel_set_params(pointer, &p)
    }

    public func setSourceGain(_ gain: Float, at index: Int) {
        tl_kernel_set_source_gain(pointer, Int32(index), gain)
    }

    public func readMeters(sourceCount: Int) -> KernelMeters {
        var m = tl_meters()
        tl_kernel_read_meters(pointer, &m)
        var out = KernelMeters()
        out.peakL = m.peak_l
        out.peakR = m.peak_r
        out.rmsL = m.rms_l
        out.rmsR = m.rms_r
        out.gainReductionDB = m.gain_reduction_db
        out.clippedSamples = m.clipped_samples
        out.ioCycles = m.io_cycles
        out.sourcePeaks = withUnsafeBytes(of: &m.source_peak) { raw in
            Array(raw.bindMemory(to: Float.self).prefix(min(sourceCount, Self.maxSources)))
        }
        return out
    }

    /// Fills `buffer` with the newest analyzer samples. `post` selects the processed signal.
    @discardableResult
    public func copyAnalyzer(post: Bool, into buffer: inout [Float]) -> UInt64 {
        let count = UInt32(min(buffer.count, Self.analyzerCapacity))
        return buffer.withUnsafeMutableBufferPointer {
            tl_kernel_copy_analyzer(pointer, post ? 1 : 0, $0.baseAddress!, count)
        }
    }
}
