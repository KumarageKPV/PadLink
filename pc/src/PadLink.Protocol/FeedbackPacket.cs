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

    /// <summary>The 8 mac bytes read as a little-endian u64, so <see cref="Write"/> puts them back as they were.</summary>
    public ulong Mac { get; init; }

    /// <summary>
    /// Reads a packet and checks its length, magic, version, type and slot. The mac needs the session
    /// key, so the caller checks it.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out FeedbackPacket packet, out PacketError error)
    {
        error = Packet.CheckHeader(source, Size, Packet.FeedbackType);
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
            Mac = BinaryPrimitives.ReadUInt64LittleEndian(source[MacOffset..]),
        };
        return true;
    }

    /// <summary>Writes the packet to the first <see cref="Size"/> bytes of destination.</summary>
    public void Write(Span<byte> destination)
    {
        Packet.WriteHeader(destination, Size, Packet.FeedbackType, Slot);
        destination[4] = LargeMotor;
        destination[5] = SmallMotor;
        destination[6] = Red;
        destination[7] = Green;
        destination[8] = Blue;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[9..], EchoMs);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[MacOffset..], Mac);
    }
}
