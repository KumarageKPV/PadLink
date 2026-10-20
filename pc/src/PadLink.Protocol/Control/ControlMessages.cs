using System.Text.Json.Serialization;

namespace PadLink.Protocol.Control;

/// <summary>
/// A control message: one flat JSON object whose <c>type</c> names it (protocol/SPEC.md, "Control
/// messages"). <see cref="ControlFrame"/> reads and writes them; the field checks live there too.
/// Byte fields hold the decoded bytes, and records compare them by reference.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(PairRequired), "pair_required")]
[JsonDerivedType(typeof(Pair), "pair")]
[JsonDerivedType(typeof(Challenge), "challenge")]
[JsonDerivedType(typeof(Auth), "auth")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(ModeChanged), "mode_changed")]
[JsonDerivedType(typeof(SlotChanged), "slot_changed")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Pong), "pong")]
[JsonDerivedType(typeof(Battery), "battery")]
[JsonDerivedType(typeof(Bye), "bye")]
[JsonDerivedType(typeof(Error), "error")]
public abstract record ControlMessage
{
    public const int NonceSize = 16;

    /// <summary>An X25519 public key.</summary>
    public const int KeySize = 32;

    /// <summary>A pairing proof or auth mac: a whole HMAC-SHA256.</summary>
    public const int HmacSize = 32;

    private protected ControlMessage() { }
}

// Fields in table order, which is the order senders write them.

public sealed record Hello(
    int Proto, string DeviceId, string DeviceName, PhonePlatform Platform, string AppVersion, byte[] Nonce)
    : ControlMessage;

public sealed record PairRequired(byte[] Key, byte[] Nonce) : ControlMessage;

public sealed record Pair(byte[] Key, byte[] Proof) : ControlMessage;

public sealed record Challenge(byte[] Nonce) : ControlMessage;

public sealed record Auth(byte[] Mac) : ControlMessage;

public sealed record Welcome(int Slot, string Colour, PadMode Mode, PadType PadType, int UdpPort) : ControlMessage;

public sealed record ModeChanged(PadMode Mode) : ControlMessage;

public sealed record SlotChanged(int Slot, string Colour, PadType PadType) : ControlMessage;

public sealed record Ping : ControlMessage;

public sealed record Pong : ControlMessage;

public sealed record Battery(int Pct) : ControlMessage;

public sealed record Bye : ControlMessage;

/// <summary>An error. Receivers treat a code they don't know as a general error.</summary>
public sealed record Error(string Code) : ControlMessage;

public static class ErrorCodes
{
    public const string UnsupportedProto = "unsupported_proto";
    public const string BadMessage = "bad_message";
    public const string BadProof = "bad_proof";
    public const string BadAuth = "bad_auth";
    public const string Full = "full";
}

[JsonConverter(typeof(PhonePlatformConverter))]
public enum PhonePlatform
{
    Android,
    Ios,
}

/// <summary>Mixed: Xbox 360 pads for the first 4 slots, then DualShock 4. AllDs4: DualShock 4 for every slot.</summary>
[JsonConverter(typeof(PadModeConverter))]
public enum PadMode
{
    Mixed,
    AllDs4,
}

[JsonConverter(typeof(PadTypeConverter))]
public enum PadType
{
    X360,
    Ds4,
}

internal sealed class PhonePlatformConverter()
    : WireNameConverter<PhonePlatform>((PhonePlatform.Android, "android"), (PhonePlatform.Ios, "ios"));

internal sealed class PadModeConverter()
    : WireNameConverter<PadMode>((PadMode.Mixed, "mixed"), (PadMode.AllDs4, "all_ds4"));

internal sealed class PadTypeConverter()
    : WireNameConverter<PadType>((PadType.X360, "x360"), (PadType.Ds4, "ds4"));
