namespace PadLink.Core.Control;

/// <summary>
/// The pairing keys of the phones that paired with this PC, by <c>deviceId</c> (protocol/SPEC.md, "Join
/// sequence"). Keys go in and out through spans, so a caller can keep its copy on the stack and clear it.
/// Calls may come from several threads.
/// </summary>
public interface IPairingStore
{
    bool Contains(string deviceId);

    /// <summary>Copies the phone's 32-byte pairing key into pairingKey, or returns false for an unknown phone.</summary>
    bool TryGetKey(string deviceId, Span<byte> pairingKey);

    /// <summary>Keeps the phone's pairing key, replacing any it had. Returns once the key is stored.</summary>
    void Save(string deviceId, string deviceName, ReadOnlySpan<byte> pairingKey);
}
