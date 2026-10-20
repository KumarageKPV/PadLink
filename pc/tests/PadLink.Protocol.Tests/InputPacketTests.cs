using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace PadLink.Protocol.Tests;

public class InputPacketTests
{
    private const string File = "input.json";

    public static TheoryData<string> Valid => Vectors.Names(File, "valid");
    public static TheoryData<string> Invalid => Vectors.Names(File, "invalid");

    [Theory]
    [MemberData(nameof(Valid))]
    public void Valid_vector_decodes_to_its_fields_and_writes_back_the_same_bytes(string name)
    {
        var vector = Vectors.Get(File, "valid", name);
        var bytes = Vectors.Hex(vector["hex"]);
        var fields = vector["fields"]!.AsObject();

        Assert.True(InputPacket.TryRead(bytes, out var packet, out var error), $"rejected for {error}");
        Assert.Equal(PacketError.None, error);
        Assert.Equal(Packet.Magic, fields["magic"]!.GetValue<byte>());
        Assert.Equal(Packet.Version, fields["version"]!.GetValue<byte>());
        Assert.Equal(Packet.InputType, fields["type"]!.GetValue<byte>());
        Assert.Equal(Expected(fields), packet);

        var written = new byte[InputPacket.Size];
        packet.Write(written);
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

        InputPacket.TryRead(Vectors.Hex(vector["hex"]), out var packet, out _);

        Assert.Equal(pressed, packet.Buttons);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_vector_is_dropped_for_its_reason(string name)
    {
        var vector = Vectors.Get(File, "invalid", name);
        var bytes = Vectors.Hex(vector["hex"]);
        var reason = vector["reason"]!.GetValue<string>();

        var read = InputPacket.TryRead(bytes, out var packet, out var error);

        if (reason == "mac")
        {
            // Checking the mac needs the session key, which PLAN.md 2.4 adds. Every other field passes.
            Assert.True(read, $"rejected for {error}");
            var written = new byte[InputPacket.Size];
            packet.Write(written);
            Assert.Equal(bytes, written);
            return;
        }
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

    [Fact]
    public void Write_refuses_a_slot_over_15()
    {
        var packet = new InputPacket { Slot = 16 };

        Assert.Throws<InvalidOperationException>(() => packet.Write(new byte[InputPacket.Size]));
    }

    [Fact]
    public void Write_refuses_a_buffer_too_small()
    {
        Assert.Throws<ArgumentException>(() => new InputPacket().Write(new byte[InputPacket.Size - 1]));
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
        Mac = BinaryPrimitives.ReadUInt64LittleEndian(Vectors.Hex(fields["mac"])),
    };
}
