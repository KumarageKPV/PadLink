using PadLink.Protocol.Control;

namespace PadLink.Protocol.Tests;

// The hot path (UDP receive → pad submit) must not allocate per packet (CLAUDE.md, Rules).
public class AllocationTests
{
    private const int Runs = 10_000;

    private static readonly byte[] SessionKey = Vectors.Hex(Vectors.File("input.json")["sessionKey"]);

    [Fact]
    public void Decoding_packets_forged_or_not_and_frame_headers_allocates_nothing()
    {
        var input = Vectors.Hex(Vectors.Get("input.json", "valid", "typical")["hex"]);
        // A flood of forged packets from the same Wi-Fi takes the mac path, so it is measured too.
        var forged = Vectors.Hex(Vectors.Get("input.json", "invalid", "mac with its last bit flipped")["hex"]);
        var feedback = Vectors.Hex(Vectors.Get("feedback.json", "valid", "typical")["hex"]);
        var frame = Vectors.Hex(Vectors.Get("control.json", "valid", "hello")["frameHex"]);
        // Each read must end where intended, so the loop measures the whole path and not an early return.
        Assert.True(InputPacket.TryRead(input, SessionKey, out _, out _));
        Assert.False(InputPacket.TryRead(forged, SessionKey, out _, out var forgedError));
        Assert.Equal(PacketError.Mac, forgedError);
        Assert.True(FeedbackPacket.TryRead(feedback, SessionKey, out _, out _));
        Assert.Equal(FrameStatus.Complete, ControlFrame.TryRead(frame, out _));
        long checksum = Decode(input, forged, feedback, frame); // warm-up: JIT and static constructors

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Runs; i++)
            checksum += Decode(input, forged, feedback, frame);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal((Runs + 1) * Decode(input, forged, feedback, frame), checksum);
    }

    [Fact]
    public void Writing_packets_with_their_mac_allocates_nothing()
    {
        Span<byte> buffer = stackalloc byte[InputPacket.Size];
        Assert.True(InputPacket.TryRead(Vectors.Hex(Vectors.Get("input.json", "valid", "typical")["hex"]), SessionKey, out var input, out _));
        Assert.True(FeedbackPacket.TryRead(Vectors.Hex(Vectors.Get("feedback.json", "valid", "typical")["hex"]), SessionKey, out var feedback, out _));
        input.Write(buffer, SessionKey);
        feedback.Write(buffer, SessionKey);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Runs; i++)
        {
            input.Write(buffer, SessionKey);
            feedback.Write(buffer, SessionKey);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static long Decode(byte[] input, byte[] forged, byte[] feedback, byte[] frame)
    {
        InputPacket.TryRead(input, SessionKey, out var inputPacket, out _);
        InputPacket.TryRead(forged, SessionKey, out _, out var forgedError);
        FeedbackPacket.TryRead(feedback, SessionKey, out var feedbackPacket, out _);
        ControlFrame.TryRead(frame, out var body);
        return inputPacket.Sequence + (long)forgedError + feedbackPacket.EchoMs + body.Length;
    }
}
