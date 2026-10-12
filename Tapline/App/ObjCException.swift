import Foundation

/// An Objective-C exception as a Swift error. AVFoundation raises these for problems it
/// only finds at run time, such as a tap format the input device no longer has.
struct ObjCException: LocalizedError {
    let name: String
    let reason: String?

    var errorDescription: String? { reason ?? name }

    /// Runs `body` and throws the Objective-C exception it raises.
    ///
    /// Swift cannot catch these exceptions, and one that unwinds through Swift code skips
    /// all of its cleanup. Out of a main-actor task, AppKit logs and swallows it, but the
    /// concurrency runtime is left pointing at the task's dead stack frame: the next
    /// main-actor check (the meter timer's, 30 times a second) reads a freed executor and
    /// crashes. `body` is unwound without cleanup too, so keep it to the calls themselves.
    static func catching<T>(_ body: () throws -> T) throws -> T {
        var result: Result<T, any Error>?
        if let exception = TaplineCatchObjCException({ result = Result(catching: body) }) {
            throw ObjCException(name: exception.name.rawValue, reason: exception.reason)
        }
        return try result!.get()
    }
}
