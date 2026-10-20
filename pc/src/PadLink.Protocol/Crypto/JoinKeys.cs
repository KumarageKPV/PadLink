using System.Security.Cryptography;
using System.Text;

namespace PadLink.Protocol.Crypto;

/// <summary>
/// The keys and MACs of the join sequence (protocol/SPEC.md, "Keys and MACs"). Inputs of the wrong size
/// throw <see cref="ArgumentException"/>; the control messages have already checked what came from the
/// network.
/// </summary>
public static class JoinKeys
{
    public const int NonceSize = 16;

    /// <summary>The size of the pairing key and the session key.</summary>
    public const int KeySize = 32;

    /// <summary>The size of the pairing proof and the auth mac.</summary>
    public const int HmacSize = HMACSHA256.HashSizeInBytes;

    public const int CodeLength = 6;

    // RFC 5869's salt when none is given. Passed explicitly, as SPEC.md asks.
    private static readonly byte[] Salt = new byte[32];

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    /// <summary>
    /// HMAC-SHA256 of the PC's then the phone's public key, keyed with the code as ASCII. Throws
    /// <see cref="ArgumentException"/> for a code that isn't 6 ASCII digits.
    /// </summary>
    public static byte[] Proof(string code, ReadOnlySpan<byte> pcPublicKey, ReadOnlySpan<byte> phonePublicKey)
    {
        if (code.Length != CodeLength || !code.All(char.IsAsciiDigit))
            throw new ArgumentException($"The code must be {CodeLength} ASCII digits.", nameof(code));
        CheckSize(pcPublicKey, X25519KeyPair.KeySize, nameof(pcPublicKey));
        CheckSize(phonePublicKey, X25519KeyPair.KeySize, nameof(phonePublicKey));
        byte[] keys = [.. pcPublicKey, .. phonePublicKey];
        return HMACSHA256.HashData(Encoding.ASCII.GetBytes(code), keys);
    }

    /// <summary>Whether proof matches the code, compared in constant time.</summary>
    public static bool CheckProof(string code, ReadOnlySpan<byte> pcPublicKey, ReadOnlySpan<byte> phonePublicKey, ReadOnlySpan<byte> proof) =>
        CryptographicOperations.FixedTimeEquals(Proof(code, pcPublicKey, phonePublicKey), proof);

    public static byte[] PairingKey(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> pcPublicKey, ReadOnlySpan<byte> phonePublicKey)
    {
        CheckSize(sharedSecret, X25519KeyPair.KeySize, nameof(sharedSecret));
        return Hkdf(sharedSecret, PairingKeyInfo(pcPublicKey, phonePublicKey));
    }

    /// <summary>The known phone's answer to <c>challenge</c>.</summary>
    public static byte[] AuthMac(ReadOnlySpan<byte> pairingKey, ReadOnlySpan<byte> pcNonce)
    {
        CheckSize(pairingKey, KeySize, nameof(pairingKey));
        CheckSize(pcNonce, NonceSize, nameof(pcNonce));
        return HMACSHA256.HashData(pairingKey, pcNonce);
    }

    /// <summary>Whether mac is the auth mac for the PC's nonce, compared in constant time.</summary>
    public static bool CheckAuthMac(ReadOnlySpan<byte> pairingKey, ReadOnlySpan<byte> pcNonce, ReadOnlySpan<byte> mac) =>
        CryptographicOperations.FixedTimeEquals(AuthMac(pairingKey, pcNonce), mac);

    /// <summary>The key for every packet mac of one session.</summary>
    public static byte[] SessionKey(ReadOnlySpan<byte> pairingKey, ReadOnlySpan<byte> pcNonce, ReadOnlySpan<byte> phoneNonce)
    {
        CheckSize(pairingKey, KeySize, nameof(pairingKey));
        return Hkdf(pairingKey, SessionKeyInfo(pcNonce, phoneNonce));
    }

    internal static byte[] PairingKeyInfo(ReadOnlySpan<byte> pcPublicKey, ReadOnlySpan<byte> phonePublicKey)
    {
        CheckSize(pcPublicKey, X25519KeyPair.KeySize, nameof(pcPublicKey));
        CheckSize(phonePublicKey, X25519KeyPair.KeySize, nameof(phonePublicKey));
        return [.. "padlink/1 pairing"u8, .. pcPublicKey, .. phonePublicKey];
    }

    internal static byte[] SessionKeyInfo(ReadOnlySpan<byte> pcNonce, ReadOnlySpan<byte> phoneNonce)
    {
        CheckSize(pcNonce, NonceSize, nameof(pcNonce));
        CheckSize(phoneNonce, NonceSize, nameof(phoneNonce));
        return [.. "padlink/1 session"u8, .. pcNonce, .. phoneNonce];
    }

    internal static void CheckSize(ReadOnlySpan<byte> value, int size, string name)
    {
        if (value.Length != size)
            throw new ArgumentException($"Must be {size} bytes, not {value.Length}.", name);
    }

    private static byte[] Hkdf(ReadOnlySpan<byte> inputKey, ReadOnlySpan<byte> info)
    {
        var key = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKey, key, Salt, info);
        return key;
    }
}
