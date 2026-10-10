import Foundation

/// "System Audio Recording" permission (TCC service kTCCServiceAudioCapture).
///
/// There is no public API to query this permission. Without it, process taps
/// silently deliver zeros while the tapped apps are muted, so we check first
/// using the TCC SPI (the same approach used by other open-source tap clients).
/// If the SPI is unavailable we fall back to `.unknown` and let the system prompt.
enum AudioCapturePermission {
    enum Status: Equatable {
        case authorized
        case denied
        case unknown
    }

    private typealias PreflightFn = @convention(c) (CFString, CFDictionary?) -> Int
    private typealias RequestFn = @convention(c) (CFString, CFDictionary?, @escaping @convention(block) (Bool) -> Void) -> Void

    private static var service: CFString { "kTCCServiceAudioCapture" as CFString }

    private nonisolated(unsafe) static let handle: UnsafeMutableRawPointer? =
        dlopen("/System/Library/PrivateFrameworks/TCC.framework/Versions/A/TCC", RTLD_NOW)

    private static var preflight: PreflightFn? {
        guard let handle, let sym = dlsym(handle, "TCCAccessPreflight") else { return nil }
        return unsafeBitCast(sym, to: PreflightFn.self)
    }

    private static var requestFn: RequestFn? {
        guard let handle, let sym = dlsym(handle, "TCCAccessRequest") else { return nil }
        return unsafeBitCast(sym, to: RequestFn.self)
    }

    /// Raw TCCAccessPreflight result, for diagnostics.
    static var rawPreflight: Int? { preflight?(service, nil) }

    static var status: Status {
        guard let preflight else { return .unknown }
        switch preflight(service, nil) {
        case 0: return .authorized
        case 1: return .denied
        default: return .unknown
        }
    }

    /// Shows the system prompt if the user has not decided yet.
    static func request() async -> Bool {
        guard let requestFn else { return false }
        return await withCheckedContinuation { continuation in
            requestFn(service, nil) { granted in
                continuation.resume(returning: granted)
            }
        }
    }

    static let settingsURL = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AudioCapture")!
}
