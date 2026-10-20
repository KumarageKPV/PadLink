using System.Security.Cryptography;

namespace PadLink.Protocol.Crypto;

/// <summary>
/// The packet mac: the first 8 bytes of HMAC-SHA256(session key, every packet byte before it)
/// (protocol/SPEC.md, "Keys and MACs"). It is on the hot path, so nothing here allocates.
/// </summary>
public static class PacketMac
{
    public static void Compute(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> data, Span<byte> mac)
    {
        CheckKey(sessionKey);
        JoinKeys.CheckSize(mac, Packet.MacSize, nameof(mac));
        Span<byte> hmac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(sessionKey, data, hmac);
        hmac[..Packet.MacSize].CopyTo(mac);
    }

    /// <summary>Whether mac is the packet mac of data, compared in constant time.</summary>
    public static bool Matches(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> mac)
    {
        Span<byte> expected = stackalloc byte[Packet.MacSize];
        Compute(sessionKey, data, expected);
        return CryptographicOperations.FixedTimeEquals(expected, mac);
    }

    internal static void CheckKey(ReadOnlySpan<byte> sessionKey) =>
        JoinKeys.CheckSize(sessionKey, JoinKeys.KeySize, nameof(sessionKey));
}
