using System.Buffers.Binary;

namespace PadLink.Protocol;

/// <summary>
/// The PC's 19-byte feedback packet: rumble, slot colour and the echo for round-trip time
/// (protocol/SPEC.md, "Feedback packet"). Reading and writing allocate nothing.
/// </summary>
public readonly record struct FeedbackPacket
{
    public const int Size = 19;

    /// <summary>The mac covers every byte before this offset.</summary>
    public const int MacOffset = 11;

    public byte Slot { get; init; }
    public byte LargeMotor { get; init; }
    public byte SmallMotor { get; init; }
    public byte Red { get; init; }
    public byte Green { get; init; }
    public byte Blue { get; init; }

    /// <summary>The <see cref="InputPacket.SentMs"/> of the last input packet the PC accepted from this phone.</summary>
    public ushort EchoMs { get; init; }

    /// <summary>Reads a packet like <see cref="InputPacket.TryRead"/>.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, ReadOnlySpan<byte> sessionKey, out FeedbackPacket packet, out PacketError error)
    {
        error = Packet.Check(source, Size, Packet.FeedbackType, sessionKey);
        if (error != PacketError.None)
        {
            packet = default;
            return false;
        }
        packet = new FeedbackPacket
        {
            Slot = source[3],
            LargeMotor = source[4],
            SmallMotor = source[5],
            Red = source[6],
            Green = source[7],
            Blue = source[8],
            EchoMs = BinaryPrimitives.ReadUInt16LittleEndian(source[9..]),
        };
        return true;
    }

    /// <summary>Writes the packet, with its mac, to the first <see cref="Size"/> bytes of destination.</summary>
    public void Write(Span<byte> destination, ReadOnlySpan<byte> sessionKey)
    {
        Packet.WriteHeader(destination, Size, Packet.FeedbackType, Slot, sessionKey);
        destination[4] = LargeMotor;
        destination[5] = SmallMotor;
        destination[6] = Red;
        destination[7] = Green;
        destination[8] = Blue;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[9..], EchoMs);
        Packet.WriteMac(destination, Size, sessionKey);
    }
}
