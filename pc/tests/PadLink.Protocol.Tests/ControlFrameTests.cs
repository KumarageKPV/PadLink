using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PadLink.Protocol.Control;

namespace PadLink.Protocol.Tests;

public class ControlFrameTests
{
    private const string File = "control.json";

    public static TheoryData<string> Valid => Vectors.Names(File, "valid");
    public static TheoryData<string> Ignored => Vectors.Names(File, "ignored");
    public static TheoryData<string> Invalid => Vectors.Names(File, "invalid");

    [Theory]
    [MemberData(nameof(Valid))]
    public void Valid_frame_decodes_to_its_message_and_encodes_back_to_it(string name)
    {
        var vector = Vectors.Get(File, "valid", name);

        var message = Decode(Vectors.Hex(vector["frameHex"]));

        Assert.True(JsonNode.DeepEquals(vector["message"], Json(message)), $"decoded as {Json(message)}");
        var again = Decode(ControlFrame.Encode(message));
        Assert.True(JsonNode.DeepEquals(vector["message"], Json(again)), $"re-encoded as {Json(again)}");
    }

    // Senders write compact JSON, type first, fields in table order, escaping only what JSON requires. So
    // where the vector's JSON is in that form, the bytes match too. Escapes for characters outside the
    // Basic Multilingual Plane may differ, and some vectors show what other senders may write.
    [Theory]
    [MemberData(nameof(Valid))]
    public void Encoding_matches_the_vector_bytes_when_its_json_is_in_sender_form(string name)
    {
        var vector = Vectors.Get(File, "valid", name);
        var json = vector["json"]!.GetValue<string>();
        if (json != SenderForm(vector["message"]!))
            return;

        var frame = ControlFrame.Encode(Decode(Vectors.Hex(vector["frameHex"])));

        Assert.Equal(json, Encoding.UTF8.GetString(frame.AsSpan(ControlFrame.HeaderSize)));
        Assert.Equal(Convert.ToHexStringLower(Vectors.Hex(vector["frameHex"])), Convert.ToHexStringLower(frame));
    }

    [Theory]
    [MemberData(nameof(Ignored))]
    public void Ignored_frame_has_no_message_and_no_fault(string name)
    {
        var frame = Vectors.Hex(Vectors.Get(File, "ignored", name)["frameHex"]);

        Assert.Equal(FrameStatus.Complete, ControlFrame.TryRead(frame, out var body));
        Assert.True(ControlFrame.TryDecode(body, out var message, out var fault));
        Assert.Null(message);
        Assert.Equal(ControlFault.None, fault);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_frame_is_rejected_for_its_reason(string name)
    {
        var vector = Vectors.Get(File, "invalid", name);
        var frame = Vectors.Hex(vector["frameHex"]);
        var reason = vector["reason"]!.GetValue<string>();

        var status = ControlFrame.TryRead(frame, out var body);

        switch (reason)
        {
            case "incomplete":
                Assert.Equal(FrameStatus.Incomplete, status);
                break;
            case "frame_empty":
                Assert.Equal(FrameStatus.Empty, status);
                break;
            case "frame_too_long":
                Assert.Equal(FrameStatus.TooLong, status);
                break;
            default:
                // These all mean bad_message, so SPEC.md lets a decoder report any of them; this one
                // tells them apart.
                Assert.Equal(FrameStatus.Complete, status);
                Assert.False(ControlFrame.TryDecode(body, out var message, out var fault));
                Assert.Null(message);
                Assert.Equal(FaultFor(reason), fault);
                break;
        }
    }

    private static ControlFault FaultFor(string reason) => reason switch
    {
        "utf8" => ControlFault.Utf8,
        "json" => ControlFault.Json,
        "name_repeated" => ControlFault.NameRepeated,
        "not_object" => ControlFault.NotObject,
        "type" => ControlFault.Type,
        "field_missing" or "field_invalid" => ControlFault.Field,
        _ => throw new ArgumentException($"Unknown reason {reason}", nameof(reason)),
    };

    [Fact]
    public void The_length_alone_decides_before_the_body_arrives()
    {
        Assert.Equal(FrameStatus.Incomplete, ControlFrame.TryRead([0x0f, 0x00, 0x00], out _));
        Assert.Equal(FrameStatus.Incomplete, ControlFrame.TryRead(Header(15), out _));
        Assert.Equal(FrameStatus.Empty, ControlFrame.TryRead(Header(0), out _));
        Assert.Equal(FrameStatus.TooLong, ControlFrame.TryRead(Header(4097), out _));
        Assert.Equal(FrameStatus.Incomplete, ControlFrame.TryRead(Header(4096), out _));
    }

    [Fact]
    public void Read_takes_one_frame_and_leaves_what_follows()
    {
        byte[] two = [.. ControlFrame.Encode(new Ping()), .. ControlFrame.Encode(new Bye())];

        Assert.Equal(FrameStatus.Complete, ControlFrame.TryRead(two, out var body));

        Assert.Equal("""{"type":"ping"}""", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void Decoded_messages_carry_typed_values()
    {
        var crypto = Vectors.File("crypto.json");

        var hello = Assert.IsType<Hello>(DecodeVector("hello"));
        Assert.Equal(1, hello.Proto);
        Assert.Equal("5b0e8f0c-6a8e-4d0a-9b8e-2f4c1d7e9a31", hello.DeviceId);
        Assert.Equal("Pixel 8", hello.DeviceName);
        Assert.Equal(PhonePlatform.Android, hello.Platform);
        Assert.Equal("1.0.0", hello.AppVersion);
        Assert.Equal(Vectors.Hex(crypto["phoneNonce"]), hello.Nonce);

        var pair = Assert.IsType<Pair>(DecodeVector("pair"));
        Assert.Equal(Vectors.Hex(crypto["phone"]!["publicKey"]), pair.Key);
        Assert.Equal(Vectors.Hex(crypto["proof"]), pair.Proof);

        var welcome = Assert.IsType<Welcome>(DecodeVector("welcome, All DualShock 4, last slot"));
        Assert.Equal(new Welcome(15, "#FF8800", PadMode.AllDs4, PadType.Ds4, 65535), welcome);

        Assert.Equal("\U0001f3ae", Assert.IsType<Error>(DecodeVector("an escaped surrogate pair")).Code);
    }

    [Fact]
    public void Encode_writes_a_little_endian_length_then_compact_json_with_type_first()
    {
        Assert.Equal("0f000000" + Convert.ToHexStringLower("""{"type":"ping"}"""u8),
            Convert.ToHexStringLower(ControlFrame.Encode(new Ping())));
        Assert.Equal("""{"type":"slot_changed","slot":4,"colour":"#00C853","padType":"ds4"}""",
            BodyText(new SlotChanged(4, "#00C853", PadType.Ds4)));
        Assert.Equal("""{"type":"error","code":"café +/<>&'"}""", BodyText(new Error("café +/<>&'")));
    }

    [Fact]
    public void Encode_refuses_a_frame_over_4096_bytes()
    {
        var shell = Encoding.UTF8.GetByteCount("""{"type":"error","code":""}""");

        Assert.Equal(ControlFrame.HeaderSize + 4096, ControlFrame.Encode(new Error(new string('x', 4096 - shell))).Length);
        Assert.Throws<ArgumentException>(() => ControlFrame.Encode(new Error(new string('x', 4097 - shell))));
    }

    [Fact]
    public void Encode_refuses_a_message_a_receiver_would_reject()
    {
        var hello = Assert.IsType<Hello>(DecodeVector("hello"));
        var welcome = new Welcome(0, "#1E90FF", PadMode.Mixed, PadType.X360, 47821);
        ControlMessage[] invalid =
        [
            hello with { Proto = 0 },
            hello with { DeviceId = "" },
            hello with { DeviceName = null! },
            hello with { DeviceName = "Ana\ud800" }, // a lone surrogate has no UTF-8 form
            hello with { DeviceName = "\udc00\ud800" },
            hello with { Platform = (PhonePlatform)2 },
            hello with { AppVersion = "" },
            hello with { Nonce = new byte[15] },
            hello with { Nonce = null! },
            new PairRequired(new byte[31], new byte[16]),
            new Pair(new byte[32], new byte[33]),
            new Challenge(new byte[17]),
            new Auth([]),
            welcome with { Slot = 16 },
            welcome with { Slot = -1 },
            welcome with { Colour = "blue" },
            welcome with { Colour = "#1E90F" },
            welcome with { Colour = "#1E90FG" },
            welcome with { Mode = (PadMode)2 },
            welcome with { PadType = (PadType)2 },
            welcome with { UdpPort = 0 },
            welcome with { UdpPort = 65536 },
            new ModeChanged((PadMode)2),
            new SlotChanged(16, "#00C853", PadType.Ds4),
            new Battery(101),
            new Battery(-1),
            new Error(""),
        ];

        foreach (var message in invalid)
            Assert.Throws<ArgumentException>(() => ControlFrame.Encode(message));
    }

    [Fact]
    public void Every_message_type_encodes_and_decodes()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var nonce = key[..16];
        ControlMessage[] messages =
        [
            new Hello(int.MaxValue, "id", "ü 🎮", PhonePlatform.Ios, "2.0", nonce),
            new PairRequired(key, nonce),
            new Pair(key, key),
            new Challenge(nonce),
            new Auth(key),
            new Welcome(7, "#abcdef", PadMode.Mixed, PadType.X360, 1),
            new ModeChanged(PadMode.AllDs4),
            new SlotChanged(0, "#000000", PadType.X360),
            new Ping(),
            new Pong(),
            new Battery(0),
            new Bye(),
            new Error(ErrorCodes.Full),
        ];

        foreach (var message in messages)
            Assert.True(JsonNode.DeepEquals(Json(message), Json(Decode(ControlFrame.Encode(message)))), $"{message}");
        Assert.Equal(13, messages.Select(message => message.GetType()).Distinct().Count());
    }

    [Theory]
    [InlineData(31, true)] // the object and 31 arrays: 32 levels
    [InlineData(32, false)]
    public void Json_nested_more_than_32_levels_is_a_bad_message(int arrays, bool accepted)
    {
        var body = Encoding.UTF8.GetBytes("""{"type":"ping","x":""" + new string('[', arrays) + new string(']', arrays) + "}");

        Assert.Equal(accepted, ControlFrame.TryDecode(body, out _, out var fault));
        Assert.Equal(accepted ? ControlFault.None : ControlFault.Json, fault);
    }

    [Theory]
    [InlineData("""{"type":"mode_changed","mode":"Mixed"}""", ControlFault.Field)] // names are case-sensitive
    [InlineData("""{"type":"mode_changed","mode":0}""", ControlFault.Field)] // no enum numbers
    [InlineData("""{"type":"slot_changed","slot":4,"colour":"#00C853","padType":"DS4"}""", ControlFault.Field)]
    [InlineData("""{"type":"hello","proto":1,"deviceId":"d","deviceName":"n","platform":"Android","appVersion":"1","nonce":"oKGio6SlpqeoqaqrrK2urw=="}""", ControlFault.Field)]
    [InlineData("""{"type":"battery","pct":"57"}""", ControlFault.Field)] // no numbers in strings
    [InlineData("""{"type":"battery","pct":null}""", ControlFault.Field)]
    [InlineData("""{"type":"battery","PCT":57}""", ControlFault.Field)] // field names are case-sensitive, so pct is missing
    [InlineData("""{"type":"battery"}""", ControlFault.Field)] // a missing number isn't 0
    [InlineData("""{"type":"slot_changed","colour":"#00C853","padType":"ds4"}""", ControlFault.Field)]
    [InlineData("""{"type":"challenge","nonce":"oKGio6SlpqeoqaqrrK2urw==\n"}""", ControlFault.Field)]
    [InlineData("""{"type":"challenge","nonce":"oKGio6SlpqeoqaqrrK2u\nrw=="}""", ControlFault.Field)]
    [InlineData("""{"type":"challenge","nonce":"=oKGio6SlpqeoqaqrrK2urw="}""", ControlFault.Field)]
    [InlineData("""{"type":"challenge","nonce":"oKGio6SlpqeoqaqrrK2urw==="}""", ControlFault.Field)]
    [InlineData("""{"type":"ping"} {}""", ControlFault.Json)] // two values
    [InlineData("""{"type":"ping",}""", ControlFault.Json)] // a trailing comma
    [InlineData("""{"type":"ping"/* a comment */}""", ControlFault.Json)]
    [InlineData("""{"type":"ping","x":"\ud800"}""", ControlFault.Utf8)]
    [InlineData("""{"type":"ping","x":"\udc00"}""", ControlFault.Utf8)]
    [InlineData("""{"type":"ping","x":"\ud800\u0041"}""", ControlFault.Utf8)]
    [InlineData("""{"type":"ping","x":"\ud800\ud800"}""", ControlFault.Utf8)]
    [InlineData("""{"type":"ping","x":"\ud800","type":"ping"}""", ControlFault.Utf8)] // the scan stops at the first fault, before the repeated type
    [InlineData("""{"type":"error","code":null}""", ControlFault.Field)]
    [InlineData("""{"type":"ping","type":5}""", ControlFault.NameRepeated)] // no object repeats a name
    [InlineData("""{"type":"bye","type":"ping"}""", ControlFault.NameRepeated)]
    [InlineData("""{"type":"ping","x":[{"a":1,"a":2}]}""", ControlFault.NameRepeated)]
    [InlineData("""{"type":"ping","x":{"\u0061":1,"a":2}}""", ControlFault.NameRepeated)] // compared after unescaping
    [InlineData("""{"type":"challenge","nonce":"oKGio6SlpqeoqaqrrK2urx=="}""", ControlFault.Field)] // an unused bit set
    [InlineData("""{"type":"challenge","nonce":"oKGio6SlpqeoqaqrrK2urw%3D%3D"}""", ControlFault.Field)]
    [InlineData("""   """, ControlFault.Json)]
    public void Frames_the_vectors_do_not_cover_are_bad_messages(string json, ControlFault expected)
    {
        Assert.False(ControlFrame.TryDecode(Encoding.UTF8.GetBytes(json), out var message, out var fault), json);
        Assert.Null(message);
        Assert.Equal(expected, fault);
    }

    // The type's value is read as the next token, so an object there must still get its own names.
    [Fact]
    public void An_object_as_the_type_value_is_a_type_fault()
    {
        Assert.False(ControlFrame.TryDecode("""{"x":{"a":1},"type":{"a":1}}"""u8, out _, out var fault));

        Assert.Equal(ControlFault.Type, fault);
    }

    [Theory]
    [InlineData("""{"type":"pong","x":"\ud83c\udfae","\u00e9":[1,{"a":null}]}""", "pong")]
    [InlineData("""{"\u0074ype":"\u0070ing"}""", "ping")] // escapes in names and the type
    [InlineData("""{"x":{"type":"bye"},"type":"ping"}""", "ping")] // only the top-level type counts
    [InlineData("""{"x":{"a":1},"type":"ping","y":[{"a":1},{"a":1}]}""", "ping")] // each object has its own names
    public void Frames_other_senders_may_write_decode(string json, string type)
    {
        Assert.True(ControlFrame.TryDecode(Encoding.UTF8.GetBytes(json), out var message, out var fault), $"{fault}");

        Assert.Equal(type, Json(message!)["type"]!.GetValue<string>());
    }

    private static ControlMessage Decode(byte[] frame)
    {
        Assert.Equal(FrameStatus.Complete, ControlFrame.TryRead(frame, out var body));
        Assert.Equal(frame.Length, ControlFrame.HeaderSize + body.Length);
        Assert.True(ControlFrame.TryDecode(body, out var message, out var fault), $"rejected for {fault}");
        Assert.Equal(ControlFault.None, fault);
        return Assert.IsAssignableFrom<ControlMessage>(message);
    }

    private static ControlMessage DecodeVector(string name) => Decode(Vectors.Hex(Vectors.Get(File, "valid", name)["frameHex"]));

    private static JsonNode Json(ControlMessage message) => JsonNode.Parse(ControlFrame.Encode(message).AsSpan(ControlFrame.HeaderSize))!;

    private static string BodyText(ControlMessage message) => Encoding.UTF8.GetString(ControlFrame.Encode(message).AsSpan(ControlFrame.HeaderSize));

    private static string SenderForm(JsonNode message) =>
        message.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static byte[] Header(uint length)
    {
        var header = new byte[ControlFrame.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        return header;
    }
}
