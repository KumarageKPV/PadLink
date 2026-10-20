using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace PadLink.Protocol.Crypto;

/// <summary>
/// An X25519 key pair (RFC 7748). The PC makes a fresh one for each pairing (protocol/SPEC.md, "Join
/// sequence"). .NET 10 has no X25519, so this uses BouncyCastle (docs/decisions.md).
/// </summary>
public sealed class X25519KeyPair
{
    public const int KeySize = 32;

    private readonly byte[] _privateKey;
    private readonly byte[] _publicKey = new byte[KeySize];

    private X25519KeyPair(byte[] privateKey)
    {
        _privateKey = privateKey;
        X25519.GeneratePublicKey(_privateKey, _publicKey);
    }

    public ReadOnlySpan<byte> PublicKey => _publicKey;

    public static X25519KeyPair Generate() => new(RandomNumberGenerator.GetBytes(KeySize));

    /// <summary>A key pair from a fixed private key, such as the vectors' RFC 7748 keys.</summary>
    public static X25519KeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        JoinKeys.CheckSize(privateKey, KeySize, nameof(privateKey));
        return new(privateKey.ToArray());
    }

    /// <summary>
    /// The shared secret with a peer's public key. False for a key whose shared secret is all zero bytes,
    /// which protocol/SPEC.md says to refuse.
    /// </summary>
    public bool TryAgree(ReadOnlySpan<byte> peerPublicKey, [NotNullWhen(true)] out byte[]? sharedSecret)
    {
        JoinKeys.CheckSize(peerPublicKey, KeySize, nameof(peerPublicKey));
        var secret = new byte[KeySize];
        // False when the result is all zero bytes.
        if (!X25519.CalculateAgreement(_privateKey, peerPublicKey, secret))
        {
            sharedSecret = null;
            return false;
        }
        sharedSecret = secret;
        return true;
    }
}
