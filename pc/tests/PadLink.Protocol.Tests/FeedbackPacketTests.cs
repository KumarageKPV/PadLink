using System.Text.Json.Nodes;

namespace PadLink.Protocol.Tests;

public class FeedbackPacketTests
{
    private const string File = "feedback.json";

    private static readonly byte[] SessionKey = Vectors.Hex(Vectors.File(File)["sessionKey"]);

    public static TheoryData<string> Valid => Vectors.Names(File, "valid");
    public static TheoryData<string> Invalid => Vectors.Names(File, "invalid");

    [Theory]
    [MemberData(nameof(Valid))]
    public void Valid_vector_decodes_to_its_fields_and_writes_back_the_same_bytes(string name)
    {
        var vector = Vectors.Get(File, "valid", name);
        var bytes = Vectors.Hex(vector["hex"]);
        var fields = vector["fields"]!.AsObject();

        Assert.True(FeedbackPacket.TryRead(bytes, SessionKey, out var packet, out var error), $"rejected for {error}");
        Assert.Equal(PacketError.None, error);
        Assert.Equal(Packet.Magic, fields["magic"]!.GetValue<byte>());
        Assert.Equal(Packet.Version, fields["version"]!.GetValue<byte>());
        Assert.Equal(Packet.FeedbackType, fields["type"]!.GetValue<byte>());
        Assert.Equal(Expected(fields), packet);

        var written = new byte[FeedbackPacket.Size];
        packet.Write(written, SessionKey);
        Assert.Equal(Convert.ToHexStringLower(bytes), Convert.ToHexStringLower(written));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_vector_is_dropped_for_its_reason(string name)
    {
        var vector = Vectors.Get(File, "invalid", name);
        var bytes = Vectors.Hex(vector["hex"]);
        var reason = vector["reason"]!.GetValue<string>();

        var read = FeedbackPacket.TryRead(bytes, SessionKey, out var packet, out var error);

        Assert.False(read);
        Assert.Equal(reason, error.ToString().ToLowerInvariant());
        Assert.Equal(default, packet);
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public void Any_flipped_mac_bit_is_dropped_for_mac(string name)
    {
        var bytes = Vectors.Hex(Vectors.Get(File, "valid", name)["hex"]);

        for (var bit = FeedbackPacket.MacOffset * 8; bit < FeedbackPacket.Size * 8; bit++)
            AssertDroppedForMac(Vectors.Flipped(bytes, bit), bit);
    }

    // The header bytes have their own checks, so a flip there may be dropped for another reason.
    [Theory]
    [MemberData(nameof(Valid))]
    public void Any_flipped_bit_between_the_header_and_the_mac_is_dropped_for_mac(string name)
    {
        var bytes = Vectors.Hex(Vectors.Get(File, "valid", name)["hex"]);

        for (var bit = 4 * 8; bit < FeedbackPacket.MacOffset * 8; bit++)
            AssertDroppedForMac(Vectors.Flipped(bytes, bit), bit);
    }

    [Fact]
    public void Reading_and_writing_need_a_32_byte_session_key()
    {
        var bytes = Vectors.Hex(Vectors.Get(File, "valid", "typical")["hex"]);

        Assert.Throws<ArgumentException>(() => FeedbackPacket.TryRead(bytes, SessionKey[..^1], out _, out _));
        Assert.Throws<ArgumentException>(() => FeedbackPacket.TryRead(bytes, [], out _, out _));
        Assert.Throws<ArgumentException>(() => new FeedbackPacket().Write(new byte[FeedbackPacket.Size], SessionKey[..^1]));
    }

    [Fact]
    public void Write_refuses_a_slot_over_15()
    {
        var packet = new FeedbackPacket { Slot = 16 };

        Assert.Throws<InvalidOperationException>(() => packet.Write(new byte[FeedbackPacket.Size], SessionKey));
    }

    [Fact]
    public void Write_refuses_a_buffer_too_small()
    {
        Assert.Throws<ArgumentException>(() => new FeedbackPacket().Write(new byte[FeedbackPacket.Size - 1], SessionKey));
    }

    private static FeedbackPacket Expected(JsonObject fields) => new()
    {
        Slot = fields["slot"]!.GetValue<byte>(),
        LargeMotor = fields["largeMotor"]!.GetValue<byte>(),
        SmallMotor = fields["smallMotor"]!.GetValue<byte>(),
        Red = fields["red"]!.GetValue<byte>(),
        Green = fields["green"]!.GetValue<byte>(),
        Blue = fields["blue"]!.GetValue<byte>(),
        EchoMs = fields["echoMs"]!.GetValue<ushort>(),
    };

    private static void AssertDroppedForMac(byte[] bytes, int bit)
    {
        Assert.False(FeedbackPacket.TryRead(bytes, SessionKey, out var packet, out var error), $"bit {bit}");
        Assert.Equal(PacketError.Mac, error);
        Assert.Equal(default, packet);
    }
}
