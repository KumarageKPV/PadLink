using System.Buffers.Binary;

namespace PadLink.Protocol;

/// <summary>
/// The phone's 30-byte input packet, which carries the whole pad state (protocol/SPEC.md, "Input
/// packet"). Reading and writing allocate nothing, since this is the hot path.
/// </summary>
public readonly record struct InputPacket
{
    public const int Size = 30;

    /// <summary>The mac covers every byte before this offset.</summary>
    public const int MacOffset = 22;

    public byte Slot { get; init; }
    public uint Sequence { get; init; }
    public PadButtons Buttons { get; init; }

    /// <summary>Left stick, right is positive.</summary>
    public short Lx { get; init; }

    /// <summary>Left stick, up is positive.</summary>
    public short Ly { get; init; }

    public short Rx { get; init; }
    public short Ry { get; init; }
    public byte Lt { get; init; }
    public byte Rt { get; init; }

    /// <summary>The phone's millisecond clock, which wraps.</summary>
    public ushort SentMs { get; init; }

    /// <summary>The 8 mac bytes read as a little-endian u64, so <see cref="Write"/> puts them back as they were.</summary>
    public ulong Mac { get; init; }

    /// <summary>
    /// Reads a packet and checks its length, magic, version, type and slot. The mac needs the session
    /// key, so the caller checks it.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out InputPacket packet, out PacketError error)
    {
        error = Packet.CheckHeader(source, Size, Packet.InputType);
        if (error != PacketError.None)
        {
            packet = default;
            return false;
        }
        packet = new InputPacket
        {
            Slot = source[3],
            Sequence = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
            Buttons = (PadButtons)BinaryPrimitives.ReadUInt16LittleEndian(source[8..]),
            Lx = BinaryPrimitives.ReadInt16LittleEndian(source[10..]),
            Ly = BinaryPrimitives.ReadInt16LittleEndian(source[12..]),
            Rx = BinaryPrimitives.ReadInt16LittleEndian(source[14..]),
            Ry = BinaryPrimitives.ReadInt16LittleEndian(source[16..]),
            Lt = source[18],
            Rt = source[19],
            SentMs = BinaryPrimitives.ReadUInt16LittleEndian(source[20..]),
            Mac = BinaryPrimitives.ReadUInt64LittleEndian(source[MacOffset..]),
        };
        return true;
    }

    /// <summary>Writes the packet to the first <see cref="Size"/> bytes of destination.</summary>
    public void Write(Span<byte> destination)
    {
        Packet.WriteHeader(destination, Size, Packet.InputType, Slot);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)Buttons);
        BinaryPrimitives.WriteInt16LittleEndian(destination[10..], Lx);
        BinaryPrimitives.WriteInt16LittleEndian(destination[12..], Ly);
        BinaryPrimitives.WriteInt16LittleEndian(destination[14..], Rx);
        BinaryPrimitives.WriteInt16LittleEndian(destination[16..], Ry);
        destination[18] = Lt;
        destination[19] = Rt;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[20..], SentMs);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[MacOffset..], Mac);
    }
}
