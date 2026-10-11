import Foundation

/// Reads a stored settings collection (a JSON object or array) entry by entry.
///
/// An entry that fails to decode (damaged, or saved by a newer Tapline) no longer takes the
/// whole collection with it, and it is not erased either: `encode` writes it back unchanged,
/// so going back to an older version and forward again loses nothing.
public struct TolerantJSON {
    /// Entries of the decoded data that could not be read, as parsed JSON.
    private var unreadableEntries: [String: Any] = [:]
    private var unreadableElements: [Any] = []

    public var unreadableCount: Int { unreadableEntries.count + unreadableElements.count }

    public init() {}

    /// Nil when `data` is not a JSON object at all.
    public mutating func decode<Value: Decodable>(_ type: [String: Value].Type, from data: Data) -> [String: Value]? {
        unreadableEntries = [:]
        guard let attempts = try? JSONDecoder().decode([String: Attempt<Value>].self, from: data) else { return nil }
        let values = attempts.compactMapValues(\.value)
        if values.count < attempts.count, let raw = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] {
            unreadableEntries = raw.filter { attempts[$0.key]?.value == nil }
        }
        return values
    }

    /// Nil when `data` is not a JSON array at all.
    public mutating func decode<Value: Decodable>(_ type: [Value].Type, from data: Data) -> [Value]? {
        unreadableElements = []
        guard let attempts = try? JSONDecoder().decode([Attempt<Value>].self, from: data) else { return nil }
        let values = attempts.compactMap(\.value)
        if values.count < attempts.count, let raw = (try? JSONSerialization.jsonObject(with: data)) as? [Any], raw.count == attempts.count {
            unreadableElements = zip(raw, attempts).filter { $1.value == nil }.map { $0.0 }
        }
        return values
    }

    /// `values` plus the unreadable entries whose keys are not taken by a value.
    public func encode<Value: Encodable>(_ values: [String: Value]) -> Data? {
        guard let data = try? JSONEncoder().encode(values) else { return nil }
        let kept = unreadableEntries.filter { values[$0.key] == nil }
        guard !kept.isEmpty, var object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return data }
        object.merge(kept) { value, _ in value }
        return try? JSONSerialization.data(withJSONObject: object)
    }

    /// `values` followed by the unreadable elements.
    public func encode<Value: Encodable>(_ values: [Value]) -> Data? {
        guard let data = try? JSONEncoder().encode(values) else { return nil }
        guard !unreadableElements.isEmpty, let array = (try? JSONSerialization.jsonObject(with: data)) as? [Any] else { return data }
        return try? JSONSerialization.data(withJSONObject: array + unreadableElements)
    }

    private struct Attempt<Value: Decodable>: Decodable {
        let value: Value?

        init(from decoder: Decoder) throws {
            value = try? Value(from: decoder)
        }
    }
}
