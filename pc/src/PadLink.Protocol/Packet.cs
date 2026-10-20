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

    internal static PacketError CheckHeader(ReadOnlySpan<byte> source, int size, byte type)
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

    internal static void WriteHeader(Span<byte> destination, int size, byte type, byte slot)
    {
        if (destination.Length < size)
            throw new ArgumentException($"A packet needs {size} bytes.", nameof(destination));
        if (slot >= SlotCount)
            throw new InvalidOperationException($"Slot {slot} is over {SlotCount - 1}.");
        destination[0] = Magic;
        destination[1] = Version;
        destination[2] = type;
        destination[3] = slot;
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
}
