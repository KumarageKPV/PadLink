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

    /// <summary>
    /// Reads a packet and checks its length, magic, version, type, slot and mac. Throws
    /// <see cref="ArgumentException"/> for a session key that isn't 32 bytes, before any other check.
    /// The exception allocates, so a receiver drops a packet whose slot has no session before calling this.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, ReadOnlySpan<byte> sessionKey, out InputPacket packet, out PacketError error)
    {
        error = Packet.Check(source, Size, Packet.InputType, sessionKey);
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
        };
        return true;
    }

    /// <summary>
    /// Whether sequence is newer than last, the sequence of the last packet accepted in the session, in
    /// serial-number order: (sequence − last) mod 2^32 is 1 to 2^31 − 1. Every sequence is newer than
    /// null, which stands for no packet yet (protocol/SPEC.md, "Packet rules").
    /// </summary>
    public static bool IsNewer(uint sequence, uint? last) =>
        last is not { } mark || unchecked(sequence - mark - 1) < int.MaxValue;

    /// <summary>Writes the packet, with its mac, to the first <see cref="Size"/> bytes of destination.</summary>
    public void Write(Span<byte> destination, ReadOnlySpan<byte> sessionKey)
    {
        Packet.WriteHeader(destination, Size, Packet.InputType, Slot, sessionKey);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)Buttons);
        BinaryPrimitives.WriteInt16LittleEndian(destination[10..], Lx);
        BinaryPrimitives.WriteInt16LittleEndian(destination[12..], Ly);
        BinaryPrimitives.WriteInt16LittleEndian(destination[14..], Rx);
        BinaryPrimitives.WriteInt16LittleEndian(destination[16..], Ry);
        destination[18] = Lt;
        destination[19] = Rt;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[20..], SentMs);
        Packet.WriteMac(destination, Size, sessionKey);
    }
}
