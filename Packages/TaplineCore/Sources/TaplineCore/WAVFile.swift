import Foundation

/// Audio from a RIFF WAVE file, as floating-point samples per channel.
public struct WAVFile: Equatable, Sendable {
    public var sampleRate: Double
    public var channels: [[Float]]

    public init(sampleRate: Double, channels: [[Float]]) {
        self.sampleRate = sampleRate
        self.channels = channels
    }

    public var frameCount: Int { channels.first?.count ?? 0 }

    public enum ParseError: Error, Equatable {
        case notWAV
        case missingChunk(String)
        case unsupportedFormat(tag: Int, bits: Int)
        case invalidHeader
        case empty
    }

    /// Reads PCM (8, 16, 24 and 32 bit), 32 and 64-bit float and WAVE_FORMAT_EXTENSIBLE files.
    /// A data chunk that runs past the end of the file is read as far as it goes.
    public static func parse(_ data: Data) throws -> WAVFile {
        let bytes = [UInt8](data)
        guard bytes.count >= 12, bytes[0..<4] == [0x52, 0x49, 0x46, 0x46], bytes[8..<12] == [0x57, 0x41, 0x56, 0x45] else {
            throw ParseError.notWAV
        }
        func u16(_ o: Int) -> Int { Int(bytes[o]) | Int(bytes[o + 1]) << 8 }
        func u32(_ o: Int) -> Int { u16(o) | u16(o + 2) << 16 }

        var format: (tag: Int, channels: Int, rate: Int, align: Int, bits: Int)?
        var pos = 12
        while pos + 8 <= bytes.count {
            let id = String(decoding: bytes[pos..<pos + 4], as: UTF8.self)
            let size = u32(pos + 4)
            let body = pos + 8
            if id == "fmt " {
                guard size >= 16, body + 16 <= bytes.count else { throw ParseError.invalidHeader }
                var tag = u16(body)
                // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                if tag == 0xFFFE {
                    guard size >= 40, body + 26 <= bytes.count else { throw ParseError.invalidHeader }
                    tag = u16(body + 24)
                }
                format = (tag, u16(body + 2), u32(body + 4), u16(body + 12), u16(body + 14))
            } else if id == "data" {
                guard let f = format else { throw ParseError.missingChunk("fmt ") }
                return try decode(bytes, from: body, count: min(size, bytes.count - body), format: f)
            }
            // Chunks are padded to an even size.
            pos = body + size + (size & 1)
        }
        throw ParseError.missingChunk(format == nil ? "fmt " : "data")
    }

    private static func decode(_ bytes: [UInt8], from start: Int, count: Int,
                               format f: (tag: Int, channels: Int, rate: Int, align: Int, bits: Int)) throws -> WAVFile {
        let width = f.bits / 8
        let supported: Bool = switch f.tag {
        case 1: [8, 16, 24, 32].contains(f.bits)
        case 3: [32, 64].contains(f.bits)
        default: false
        }
        guard supported else { throw ParseError.unsupportedFormat(tag: f.tag, bits: f.bits) }
        guard f.channels > 0, f.rate > 0, f.align >= width * f.channels else { throw ParseError.invalidHeader }
        let frames = count / f.align
        guard frames > 0 else { throw ParseError.empty }

        var channels = [[Float]](repeating: [Float](repeating: 0, count: frames), count: f.channels)
        bytes.withUnsafeBytes { raw in
            for i in 0..<frames {
                for c in 0..<f.channels {
                    let o = start + i * f.align + c * width
                    let v: Float
                    switch (f.tag, f.bits) {
                    case (1, 8):
                        v = (Float(raw[o]) - 128) / 128
                    case (1, 16):
                        v = Float(Int16(bitPattern: UInt16(raw[o]) | UInt16(raw[o + 1]) << 8)) / 32768
                    case (1, 24):
                        let u = UInt32(raw[o]) << 8 | UInt32(raw[o + 1]) << 16 | UInt32(raw[o + 2]) << 24
                        v = Float(Int32(bitPattern: u) >> 8) / 8_388_608
                    case (1, 32):
                        v = Float(Double(Int32(bitPattern: raw.loadUnaligned(fromByteOffset: o, as: UInt32.self).littleEndian)) / 2_147_483_648)
                    case (3, 32):
                        v = Float(bitPattern: raw.loadUnaligned(fromByteOffset: o, as: UInt32.self).littleEndian)
                    default:
                        v = Float(Double(bitPattern: raw.loadUnaligned(fromByteOffset: o, as: UInt64.self).littleEndian))
                    }
                    channels[c][i] = v.isFinite ? v : 0
                }
            }
        }
        return WAVFile(sampleRate: Double(f.rate), channels: channels)
    }

    /// 32-bit float WAV.
    public func encoded() -> Data {
        let count = channels.count
        let frames = frameCount
        var out = Data()
        func append32(_ v: Int) { withUnsafeBytes(of: UInt32(truncatingIfNeeded: v).littleEndian) { out.append(contentsOf: $0) } }
        func append16(_ v: Int) { withUnsafeBytes(of: UInt16(truncatingIfNeeded: v).littleEndian) { out.append(contentsOf: $0) } }
        let dataSize = frames * count * 4
        out.append(contentsOf: Array("RIFF".utf8))
        append32(4 + 8 + 16 + 8 + dataSize)
        out.append(contentsOf: Array("WAVEfmt ".utf8))
        append32(16)
        append16(3)
        append16(count)
        append32(Int(sampleRate.rounded()))
        append32(Int(sampleRate.rounded()) * count * 4)
        append16(count * 4)
        append16(32)
        out.append(contentsOf: Array("data".utf8))
        append32(dataSize)
        var samples = [UInt32]()
        samples.reserveCapacity(frames * count)
        for i in 0..<frames {
            for c in 0..<count { samples.append(channels[c][i].bitPattern.littleEndian) }
        }
        samples.withUnsafeBytes { out.append(contentsOf: $0) }
        return out
    }
}
