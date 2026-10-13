import AudioToolbox
import Synchronization
import TaplineCore

/// Parameter changes the host schedules on the render thread (automation), handed to the engine
/// without locks or allocation: the render block posts them, the engine's timer takes them.
/// Also the host's bypass, which the render block reads.
final class ParameterInbox: Sendable {
    private let strength = Atomic<UInt32>(0)
    private let eq = Atomic<UInt32>(0)
    private let virtualSpeakers = Atomic<UInt32>(0)
    private let listenAs = Atomic<UInt32>(0)
    private let filterPhase = Atomic<UInt32>(0)
    /// One bit per parameter address that has a value waiting.
    private let waiting = Atomic<UInt32>(0)

    /// The host bypasses the effect: the render block passes the input through.
    let bypassed = Atomic<Bool>(false)

    /// Render thread.
    func post(address: AUParameterAddress, value: AUValue) {
        let bits = value.bitPattern
        switch PluginParameter(rawValue: address) {
        case .strength: strength.store(bits, ordering: .relaxed)
        case .eq: eq.store(bits, ordering: .relaxed)
        case .virtualSpeakers: virtualSpeakers.store(bits, ordering: .relaxed)
        case .listenAs: listenAs.store(bits, ordering: .relaxed)
        case .filterPhase: filterPhase.store(bits, ordering: .relaxed)
        case nil: return
        }
        waiting.bitwiseOr(1 << UInt32(address), ordering: .releasing)
    }

    /// The parameters that changed since the last call, with their newest values.
    func take() -> [(PluginParameter, AUValue)] {
        let bits = waiting.exchange(0, ordering: .acquiring)
        guard bits != 0 else { return [] }
        return PluginParameter.allCases.compactMap { parameter in
            guard bits & (1 << UInt32(parameter.rawValue)) != 0 else { return nil }
            let value: UInt32 = switch parameter {
            case .strength: strength.load(ordering: .relaxed)
            case .eq: eq.load(ordering: .relaxed)
            case .virtualSpeakers: virtualSpeakers.load(ordering: .relaxed)
            case .listenAs: listenAs.load(ordering: .relaxed)
            case .filterPhase: filterPhase.load(ordering: .relaxed)
            }
            return (parameter, AUValue(bitPattern: value))
        }
    }
}
