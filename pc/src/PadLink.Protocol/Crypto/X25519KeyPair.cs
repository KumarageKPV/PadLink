using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace PadLink.Protocol.Crypto;

/// <summary>
/// An X25519 key pair (RFC 7748). The PC makes a fresh one for each pairing (protocol/SPEC.md, "Join
/// sequence"). .NET 10 has no X25519, so this uses BouncyCastle (docs/decisions.md).
/// </summary>
/// <remarks>
/// Dispose it once the pairing key is derived: that overwrites the private key. Keep the shared secret
/// in a span that is cleared after use, such as a stackalloc.
/// </remarks>
public sealed class X25519KeyPair : IDisposable
{
    public const int KeySize = 32;

    // Pinned, so the GC leaves no copies of the private key that Dispose can't clear.
    private readonly byte[] _privateKey = GC.AllocateUninitializedArray<byte>(KeySize, pinned: true);
    private readonly byte[] _publicKey = new byte[KeySize];
    private bool _disposed;

    private X25519KeyPair(ReadOnlySpan<byte> privateKey)
    {
        privateKey.CopyTo(_privateKey);
        X25519.GeneratePublicKey(_privateKey, _publicKey);
    }

    public ReadOnlySpan<byte> PublicKey => _publicKey;

    /// <summary>For tests: all zero bytes once disposed.</summary>
    internal ReadOnlySpan<byte> PrivateKey => _privateKey;

    public static X25519KeyPair Generate()
    {
        Span<byte> privateKey = stackalloc byte[KeySize];
        RandomNumberGenerator.Fill(privateKey);
        try
        {
            return new(privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <summary>A key pair from a fixed private key, such as the vectors' RFC 7748 keys.</summary>
    public static X25519KeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        JoinKeys.CheckSize(privateKey, KeySize, nameof(privateKey));
        return new(privateKey);
    }

    /// <summary>
    /// Writes the shared secret with a peer's public key into sharedSecret, 32 bytes. False, with
    /// sharedSecret all zero bytes, for a key whose shared secret is all zero, which protocol/SPEC.md
    /// says to refuse.
    /// </summary>
    public bool TryAgree(ReadOnlySpan<byte> peerPublicKey, Span<byte> sharedSecret)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        JoinKeys.CheckSize(peerPublicKey, KeySize, nameof(peerPublicKey));
        JoinKeys.CheckSize(sharedSecret, KeySize, nameof(sharedSecret));
        // False when the result is all zero bytes.
        return X25519.CalculateAgreement(_privateKey, peerPublicKey, sharedSecret);
    }

    public void Dispose()
    {
        _disposed = true;
        CryptographicOperations.ZeroMemory(_privateKey);
    }
}
