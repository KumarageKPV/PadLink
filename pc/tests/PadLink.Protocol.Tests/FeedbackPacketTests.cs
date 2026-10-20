using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace PadLink.Protocol.Tests;

public class FeedbackPacketTests
{
    private const string File = "feedback.json";

    public static TheoryData<string> Valid => Vectors.Names(File, "valid");
    public static TheoryData<string> Invalid => Vectors.Names(File, "invalid");

    [Theory]
    [MemberData(nameof(Valid))]
    public void Valid_vector_decodes_to_its_fields_and_writes_back_the_same_bytes(string name)
    {
        var vector = Vectors.Get(File, "valid", name);
        var bytes = Vectors.Hex(vector["hex"]);
        var fields = vector["fields"]!.AsObject();

        Assert.True(FeedbackPacket.TryRead(bytes, out var packet, out var error), $"rejected for {error}");
        Assert.Equal(PacketError.None, error);
        Assert.Equal(Packet.Magic, fields["magic"]!.GetValue<byte>());
        Assert.Equal(Packet.Version, fields["version"]!.GetValue<byte>());
        Assert.Equal(Packet.FeedbackType, fields["type"]!.GetValue<byte>());
        Assert.Equal(Expected(fields), packet);

        var written = new byte[FeedbackPacket.Size];
        packet.Write(written);
        Assert.Equal(Convert.ToHexStringLower(bytes), Convert.ToHexStringLower(written));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_vector_is_dropped_for_its_reason(string name)
    {
        var vector = Vectors.Get(File, "invalid", name);
        var bytes = Vectors.Hex(vector["hex"]);
        var reason = vector["reason"]!.GetValue<string>();

        var read = FeedbackPacket.TryRead(bytes, out var packet, out var error);

        if (reason == "mac")
        {
            // Checking the mac needs the session key, which PLAN.md 2.4 adds. Every other field passes.
            Assert.True(read, $"rejected for {error}");
            var written = new byte[FeedbackPacket.Size];
            packet.Write(written);
            Assert.Equal(bytes, written);
            return;
        }
        Assert.False(read);
        Assert.Equal(reason, error.ToString().ToLowerInvariant());
        Assert.Equal(default, packet);
    }

    [Fact]
    public void Write_refuses_a_slot_over_15()
    {
        var packet = new FeedbackPacket { Slot = 16 };

        Assert.Throws<InvalidOperationException>(() => packet.Write(new byte[FeedbackPacket.Size]));
    }

    [Fact]
    public void Write_refuses_a_buffer_too_small()
    {
        Assert.Throws<ArgumentException>(() => new FeedbackPacket().Write(new byte[FeedbackPacket.Size - 1]));
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
        Mac = BinaryPrimitives.ReadUInt64LittleEndian(Vectors.Hex(fields["mac"])),
    };
}
