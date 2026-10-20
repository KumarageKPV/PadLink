using PadLink.Protocol.Crypto;

namespace PadLink.Protocol;

/// <summary>What the input and feedback packets share (protocol/SPEC.md, "Packet rules").</summary>
public static class Packet
{
    public const byte Magic = 0x50;

    /// <summary>The packet version, and the <c>proto</c> number in <c>hello</c>.</summary>
    public const byte Version = 1;

    public const byte InputType = 0x01;
    public const byte FeedbackType = 0x02;
    public const int SlotCount = 16;
    public const int MacSize = 8;

    /// <summary>Checks the length, magic, version, type and slot, then the mac.</summary>
    internal static PacketError Check(ReadOnlySpan<byte> source, int size, byte type, ReadOnlySpan<byte> sessionKey)
    {
        PacketMac.CheckKey(sessionKey);
        var error = CheckHeader(source, size, type);
        if (error != PacketError.None)
            return error;
        var macOffset = size - MacSize;
        return PacketMac.Matches(sessionKey, source[..macOffset], source[macOffset..]) ? PacketError.None : PacketError.Mac;
    }

    internal static void WriteHeader(Span<byte> destination, int size, byte type, byte slot, ReadOnlySpan<byte> sessionKey)
    {
        if (destination.Length < size)
            throw new ArgumentException($"A packet needs {size} bytes.", nameof(destination));
        if (slot >= SlotCount)
            throw new InvalidOperationException($"Slot {slot} is over {SlotCount - 1}.");
        PacketMac.CheckKey(sessionKey);
        destination[0] = Magic;
        destination[1] = Version;
        destination[2] = type;
        destination[3] = slot;
    }

    /// <summary>Writes the mac of the bytes before it, once the caller has written them.</summary>
    internal static void WriteMac(Span<byte> destination, int size, ReadOnlySpan<byte> sessionKey)
    {
        var macOffset = size - MacSize;
        PacketMac.Compute(sessionKey, destination[..macOffset], destination.Slice(macOffset, MacSize));
    }

    private static PacketError CheckHeader(ReadOnlySpan<byte> source, int size, byte type)
    {
        if (source.Length != size)
            return PacketError.Length;
        if (source[0] != Magic)
            return PacketError.Magic;
        if (source[1] != Version)
            return PacketError.Version;
        if (source[2] != type)
            return PacketError.Type;
        if (source[3] >= SlotCount)
            return PacketError.Slot;
        return PacketError.None;
    }
}

/// <summary>Why a packet was dropped. The names are the reasons the vectors use.</summary>
public enum PacketError : byte
{
    None,
    Length,
    Magic,
    Version,
    Type,
    Slot,
    Mac,
}
