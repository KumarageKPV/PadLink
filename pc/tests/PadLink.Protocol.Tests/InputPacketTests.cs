using System.Text.Json.Nodes;

namespace PadLink.Protocol.Tests;

public class InputPacketTests
{
    private const string File = "input.json";

    private static readonly byte[] SessionKey = Vectors.Hex(Vectors.File(File)["sessionKey"]);

    public static TheoryData<string> Valid => Vectors.Names(File, "valid");
    public static TheoryData<string> Invalid => Vectors.Names(File, "invalid");

    public static TheoryData<uint?, uint, bool> SequenceOrder
    {
        get
        {
            var cases = new TheoryData<uint?, uint, bool>();
            foreach (var order in Vectors.File(File)["sequenceOrder"]!.AsArray())
                cases.Add(order!["last"]?.GetValue<uint>(), order["sequence"]!.GetValue<uint>(), order["newer"]!.GetValue<bool>());
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void Valid_vector_decodes_to_its_fields_and_writes_back_the_same_bytes(string name)
    {
        var vector = Vectors.Get(File, "valid", name);
        var bytes = Vectors.Hex(vector["hex"]);
        var fields = vector["fields"]!.AsObject();

        Assert.True(InputPacket.TryRead(bytes, SessionKey, out var packet, out var error), $"rejected for {error}");
        Assert.Equal(PacketError.None, error);
        Assert.Equal(Packet.Magic, fields["magic"]!.GetValue<byte>());
        Assert.Equal(Packet.Version, fields["version"]!.GetValue<byte>());
        Assert.Equal(Packet.InputType, fields["type"]!.GetValue<byte>());
        Assert.Equal(Expected(fields), packet);

        var written = new byte[InputPacket.Size];
        packet.Write(written, SessionKey);
        Assert.Equal(Convert.ToHexStringLower(bytes), Convert.ToHexStringLower(written));
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void Pressed_names_are_the_set_button_bits(string name)
    {
        var vector = Vectors.Get(File, "valid", name);
        var pressed = PadButtons.None;
        foreach (var button in vector["pressed"]!.AsArray())
            pressed |= Enum.Parse<PadButtons>(button!.GetValue<string>(), ignoreCase: true);

        Assert.True(InputPacket.TryRead(Vectors.Hex(vector["hex"]), SessionKey, out var packet, out _));

        Assert.Equal(pressed, packet.Buttons);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_vector_is_dropped_for_its_reason(string name)
    {
        var vector = Vectors.Get(File, "invalid", name);
        var bytes = Vectors.Hex(vector["hex"]);
        var reason = vector["reason"]!.GetValue<string>();

        var read = InputPacket.TryRead(bytes, SessionKey, out var packet, out var error);

        Assert.False(read);
        Assert.Equal(reason, error.ToString().ToLowerInvariant());
        Assert.Equal(default, packet);
    }

    [Fact]
    public void Button_bits_match_the_vectors()
    {
        var bits = Vectors.File(File)["buttonBits"]!.AsObject();

        Assert.Equal(16, bits.Count);
        foreach (var (name, bit) in bits)
            Assert.Equal(1 << bit!.GetValue<int>(), (int)Enum.Parse<PadButtons>(name, ignoreCase: true));
        Assert.Equal(17, Enum.GetValues<PadButtons>().Length); // the 16 bits and None
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void Any_flipped_mac_bit_is_dropped_for_mac(string name)
    {
        var bytes = Vectors.Hex(Vectors.Get(File, "valid", name)["hex"]);

        for (var bit = InputPacket.MacOffset * 8; bit < InputPacket.Size * 8; bit++)
            AssertDroppedForMac(Vectors.Flipped(bytes, bit), bit);
    }

    // The header bytes have their own checks, so a flip there may be dropped for another reason.
    [Theory]
    [MemberData(nameof(Valid))]
    public void Any_flipped_bit_between_the_header_and_the_mac_is_dropped_for_mac(string name)
    {
        var bytes = Vectors.Hex(Vectors.Get(File, "valid", name)["hex"]);

        for (var bit = 4 * 8; bit < InputPacket.MacOffset * 8; bit++)
            AssertDroppedForMac(Vectors.Flipped(bytes, bit), bit);
    }

    [Theory]
    [MemberData(nameof(SequenceOrder))]
    public void A_sequence_is_newer_in_serial_number_order(uint? last, uint sequence, bool newer)
    {
        Assert.Equal(newer, InputPacket.IsNewer(sequence, last));
    }

    [Fact]
    public void Reading_and_writing_need_a_32_byte_session_key()
    {
        var bytes = Vectors.Hex(Vectors.Get(File, "valid", "typical")["hex"]);

        Assert.Throws<ArgumentException>(() => InputPacket.TryRead(bytes, SessionKey[..^1], out _, out _));
        Assert.Throws<ArgumentException>(() => InputPacket.TryRead(bytes, [], out _, out _));
        Assert.Throws<ArgumentException>(() => new InputPacket().Write(new byte[InputPacket.Size], SessionKey[..^1]));
    }

    [Fact]
    public void Write_refuses_a_slot_over_15()
    {
        var packet = new InputPacket { Slot = 16 };

        Assert.Throws<InvalidOperationException>(() => packet.Write(new byte[InputPacket.Size], SessionKey));
    }

    [Fact]
    public void Write_refuses_a_buffer_too_small()
    {
        Assert.Throws<ArgumentException>(() => new InputPacket().Write(new byte[InputPacket.Size - 1], SessionKey));
    }

    private static InputPacket Expected(JsonObject fields) => new()
    {
        Slot = fields["slot"]!.GetValue<byte>(),
        Sequence = fields["sequence"]!.GetValue<uint>(),
        Buttons = (PadButtons)fields["buttons"]!.GetValue<ushort>(),
        Lx = fields["lx"]!.GetValue<short>(),
        Ly = fields["ly"]!.GetValue<short>(),
        Rx = fields["rx"]!.GetValue<short>(),
        Ry = fields["ry"]!.GetValue<short>(),
        Lt = fields["lt"]!.GetValue<byte>(),
        Rt = fields["rt"]!.GetValue<byte>(),
        SentMs = fields["sentMs"]!.GetValue<ushort>(),
    };

    private static void AssertDroppedForMac(byte[] bytes, int bit)
    {
        Assert.False(InputPacket.TryRead(bytes, SessionKey, out var packet, out var error), $"bit {bit}");
        Assert.Equal(PacketError.Mac, error);
        Assert.Equal(default, packet);
    }
}
