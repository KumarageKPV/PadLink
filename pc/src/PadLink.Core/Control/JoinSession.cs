using System.Net;
using System.Security.Cryptography;
using PadLink.Protocol;
using PadLink.Protocol.Control;
using PadLink.Protocol.Crypto;

namespace PadLink.Core.Control;

internal enum JoinState
{
    AwaitingHello,

    /// <summary>A new phone got <c>pair_required</c>.</summary>
    AwaitingPair,

    /// <summary>A known phone got <c>challenge</c>.</summary>
    AwaitingAuth,

    /// <summary>The phone got <c>welcome</c>.</summary>
    Joined,

    Closed,
}

/// <summary>What to do after a frame: send the reply, if any, then close if asked.</summary>
internal readonly record struct JoinStep(ControlMessage? Reply, bool Close)
{
    public static readonly JoinStep None = new(null, false);

    public static JoinStep Send(ControlMessage reply) => new(reply, false);

    /// <summary>Every error the PC sends ends the connection (protocol/SPEC.md, "Join rules").</summary>
    public static JoinStep Fail(string code) => new(new Error(code), true);
}

/// <summary>Called once a phone passes <c>pair</c> or <c>auth</c>: its welcome, or null for <c>full</c>.</summary>
internal delegate Welcome? JoinCallback(ConnectedPhone phone, ReadOnlySpan<byte> sessionKey);

/// <summary>
/// The PC's side of one control connection, without the socket: frame bodies in, replies out
/// (protocol/SPEC.md, "Join sequence" and "Join rules"). One thread at a time calls it.
/// </summary>
internal sealed class JoinSession(IPAddress phoneAddress, PairingCode code, IPairingStore store, JoinCallback join) : IDisposable
{
    private Hello? _hello;
    private byte[]? _pcNonce;
    private X25519KeyPair? _keys; // made for this pairing, and disposed once it is over

    public JoinState State { get; private set; }

    /// <summary>The phone, once welcomed.</summary>
    public ConnectedPhone? Phone { get; private set; }

    /// <summary>Handles one frame body. Throws <see cref="InvalidOperationException"/> once closed.</summary>
    public JoinStep Receive(ReadOnlySpan<byte> body)
    {
        if (State == JoinState.Closed)
            throw new InvalidOperationException("The session is closed.");
        var step = !ControlFrame.TryDecode(body, out var message, out _)
            ? JoinStep.Fail(ErrorCodes.BadMessage)
            : message switch
            {
                null => JoinStep.None, // a type this version doesn't know
                Ping => JoinStep.Send(new Pong()),
                Pong or Error => JoinStep.None,
                Bye => new JoinStep(null, Close: true),
                Hello hello when State == JoinState.AwaitingHello => OnHello(hello),
                Pair pair when State == JoinState.AwaitingPair => OnPair(pair),
                Auth auth when State == JoinState.AwaitingAuth => OnAuth(auth),
                Battery battery when State == JoinState.Joined => OnBattery(battery),
                // Out of place: a join message after welcome, before its turn or twice, or one the PC sends.
                _ => JoinStep.Fail(ErrorCodes.BadMessage),
            };
        if (step.Close)
        {
            State = JoinState.Closed;
            DisposeKeys();
        }
        return step;
    }

    public void Dispose() => DisposeKeys();

    private JoinStep OnHello(Hello hello)
    {
        if (hello.Proto != Packet.Version)
            return JoinStep.Fail(ErrorCodes.UnsupportedProto);
        _hello = hello;
        _pcNonce = JoinKeys.NewNonce();
        if (store.Contains(hello.DeviceId))
        {
            State = JoinState.AwaitingAuth;
            return JoinStep.Send(new Challenge(_pcNonce));
        }
        _keys = X25519KeyPair.Generate();
        State = JoinState.AwaitingPair;
        return JoinStep.Send(new PairRequired(_keys.PublicKey.ToArray(), _pcNonce));
    }

    private JoinStep OnPair(Pair pair)
    {
        Span<byte> secret = stackalloc byte[X25519KeyPair.KeySize];
        try
        {
            // Before the proof, so a key like this costs no try.
            if (!_keys!.TryAgree(pair.Key, secret))
                return JoinStep.Fail(ErrorCodes.BadMessage);
            if (!code.TryUse(_keys.PublicKey, pair.Key, pair.Proof))
                return JoinStep.Fail(ErrorCodes.BadProof);
            var pairingKey = JoinKeys.PairingKey(secret, _keys.PublicKey, pair.Key);
            try
            {
                store.Save(_hello!.DeviceId, _hello.DeviceName, pairingKey);
                return Join(pairingKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pairingKey);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            DisposeKeys();
        }
    }

    private JoinStep OnAuth(Auth auth)
    {
        Span<byte> pairingKey = stackalloc byte[JoinKeys.KeySize];
        try
        {
            // The store may have forgotten the phone since hello.
            if (!store.TryGetKey(_hello!.DeviceId, pairingKey) || !JoinKeys.CheckAuthMac(pairingKey, _pcNonce, auth.Mac))
                return JoinStep.Fail(ErrorCodes.BadAuth);
            return Join(pairingKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pairingKey);
        }
    }

    private JoinStep OnBattery(Battery battery)
    {
        Phone!.Battery = battery.Pct;
        return JoinStep.None;
    }

    private JoinStep Join(ReadOnlySpan<byte> pairingKey)
    {
        var sessionKey = JoinKeys.SessionKey(pairingKey, _pcNonce, _hello!.Nonce);
        try
        {
            var phone = new ConnectedPhone(_hello, phoneAddress);
            if (join(phone, sessionKey) is not { } welcome)
                return JoinStep.Fail(ErrorCodes.Full);
            Phone = phone;
            State = JoinState.Joined;
            return JoinStep.Send(welcome);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
    }

    private void DisposeKeys()
    {
        _keys?.Dispose();
        _keys = null;
    }
}
