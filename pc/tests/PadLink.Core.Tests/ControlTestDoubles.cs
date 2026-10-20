using PadLink.Core.Control;
using PadLink.Protocol.Control;
using PadLink.Protocol.Crypto;

namespace PadLink.Core.Tests;

/// <summary>The phone's side of the join (protocol/SPEC.md, "Join sequence"), with its keys kept for checks.</summary>
internal sealed class PhoneSide(string deviceId = PhoneSide.DefaultDeviceId)
{
    public const string DefaultDeviceId = "5b0e8f0c-6a8e-4d0a-9b8e-2f4c1d7e9a31";
    public const string Name = "Ana's phone";

    public string DeviceId { get; } = deviceId;

    public byte[] Nonce { get; } = JoinKeys.NewNonce();

    public byte[]? PairingKey { get; private set; }

    public byte[]? SessionKey { get; private set; }

    public Hello Hello(int proto = 1) => new(proto, DeviceId, Name, PhonePlatform.Android, "1.0.0", Nonce);

    /// <summary>The pair message for a pair_required, made with the given code.</summary>
    public Pair Pair(PairRequired request, string code)
    {
        using var keys = X25519KeyPair.Generate();
        var secret = new byte[X25519KeyPair.KeySize];
        Assert.True(keys.TryAgree(request.Key, secret));
        PairingKey = JoinKeys.PairingKey(secret, request.Key, keys.PublicKey);
        SessionKey = JoinKeys.SessionKey(PairingKey, request.Nonce, Nonce);
        return new Pair(keys.PublicKey.ToArray(), JoinKeys.Proof(code, request.Key, keys.PublicKey));
    }

    /// <summary>The auth message for a challenge, made with the pairing key from an earlier pairing.</summary>
    public Auth Auth(Challenge challenge)
    {
        SessionKey = JoinKeys.SessionKey(PairingKey!, challenge.Nonce, Nonce);
        return new Auth(JoinKeys.AuthMac(PairingKey!, challenge.Nonce));
    }

    /// <summary>A phone that paired with this PC earlier: the store holds its key.</summary>
    public static PhoneSide Paired(IPairingStore store, string deviceId = DefaultDeviceId)
    {
        var phone = new PhoneSide(deviceId) { PairingKey = Enumerable.Range(0, JoinKeys.KeySize).Select(i => (byte)(i + deviceId.Length)).ToArray() };
        store.Save(deviceId, Name, phone.PairingKey);
        return phone;
    }

    /// <summary>The same phone in a new connection: same id and keys, a fresh nonce.</summary>
    public PhoneSide Reconnect() => new(DeviceId) { PairingKey = PairingKey };
}

internal sealed class MemoryPairingStore : IPairingStore
{
    private readonly Dictionary<string, byte[]> _keys = [];

    public bool Contains(string deviceId)
    {
        lock (_keys)
            return _keys.ContainsKey(deviceId);
    }

    public bool TryGetKey(string deviceId, Span<byte> pairingKey)
    {
        lock (_keys)
        {
            if (!_keys.TryGetValue(deviceId, out var key))
                return false;
            key.CopyTo(pairingKey);
            return true;
        }
    }

    public void Save(string deviceId, string deviceName, ReadOnlySpan<byte> pairingKey)
    {
        lock (_keys)
            _keys[deviceId] = pairingKey.ToArray();
    }

    public byte[]? KeyOf(string deviceId)
    {
        lock (_keys)
            return _keys.GetValueOrDefault(deviceId);
    }
}

/// <summary>
/// Gives each phone the lowest free slot, or its own slot back when the same device joins again, and
/// records every call in order.
/// </summary>
internal sealed class RecordingJoinHandler : IJoinHandler
{
    public const int UdpPort = 47821;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly ConnectedPhone?[] _slots;
    private readonly List<string> _calls = [];
    private readonly List<(ConnectedPhone Phone, byte[] SessionKey)> _joins = [];
    private readonly List<ConnectedPhone> _leaves = [];

    public RecordingJoinHandler(int slotCount = 16)
    {
        _slots = new ConnectedPhone?[slotCount];
    }

    /// <summary>A device whose join throws, as a failing driver would.</summary>
    public string? FailFor { get; init; }

    /// <summary>"join &lt;deviceId&gt; &lt;slot&gt;", "full &lt;deviceId&gt;" and "leave &lt;deviceId&gt;", in order.</summary>
    public string[] Calls
    {
        get
        {
            lock (_gate)
                return [.. _calls];
        }
    }

    public (ConnectedPhone Phone, byte[] SessionKey)[] Joins
    {
        get
        {
            lock (_gate)
                return [.. _joins];
        }
    }

    public ConnectedPhone[] Leaves
    {
        get
        {
            lock (_gate)
                return [.. _leaves];
        }
    }

    public Welcome? Join(ConnectedPhone phone, ReadOnlySpan<byte> sessionKey)
    {
        if (phone.DeviceId == FailFor)
            throw new InvalidOperationException($"Join failed for {phone.DeviceId}.");
        lock (_gate)
        {
            var slot = Array.FindIndex(_slots, holder => holder?.DeviceId == phone.DeviceId);
            if (slot < 0)
                slot = Array.IndexOf(_slots, null);
            if (slot < 0)
            {
                _calls.Add($"full {phone.DeviceId}");
                return null;
            }
            _slots[slot] = phone;
            _calls.Add($"join {phone.DeviceId} {slot}");
            _joins.Add((phone, sessionKey.ToArray()));
            return new Welcome(slot, "#FF8000", PadMode.AllDs4, PadType.Ds4, UdpPort);
        }
    }

    public void Leave(ConnectedPhone phone)
    {
        lock (_gate)
        {
            var slot = Array.IndexOf(_slots, phone);
            if (slot >= 0)
                _slots[slot] = null;
            _calls.Add($"leave {phone.DeviceId}");
            _leaves.Add(phone);
        }
    }

    /// <summary>Waits until the calls so far satisfy the condition, or fails.</summary>
    public void WaitUntil(Func<string[], bool> condition)
    {
        if (!SpinWait.SpinUntil(() => condition(Calls), Timeout))
            Assert.Fail($"Timed out; the calls are [{string.Join(", ", Calls)}].");
    }
}

internal static class Bodies
{
    /// <summary>The JSON body of a message's frame.</summary>
    public static byte[] Of(ControlMessage message) => ControlFrame.Encode(message)[ControlFrame.HeaderSize..];
}
