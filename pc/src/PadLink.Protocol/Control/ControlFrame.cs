using System.Buffers;
using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;

namespace PadLink.Protocol.Control;

/// <summary>Where <see cref="ControlFrame.TryRead"/> got to. The names match the vectors' reasons.</summary>
public enum FrameStatus
{
    Complete,

    /// <summary>Wait for more bytes.</summary>
    Incomplete,

    /// <summary>Length 0: close the connection without reading on.</summary>
    Empty,

    /// <summary>Length over 4096: close the connection without reading the body.</summary>
    TooLong,
}

/// <summary>Why a frame body is a bad message. Every fault gets <c>error</c> <c>bad_message</c>, then the connection closes.</summary>
public enum ControlFault
{
    None,

    /// <summary>Not UTF-8, or a string escape that stands for a lone surrogate.</summary>
    Utf8,

    /// <summary>Not JSON, or nested more than 32 levels deep.</summary>
    Json,

    /// <summary>An object, at any depth, repeats a name.</summary>
    NameRepeated,

    NotObject,

    /// <summary>No string <c>type</c> field.</summary>
    Type,

    /// <summary>A known message with a missing or invalid field.</summary>
    Field,
}

/// <summary>
/// Control-message framing over TCP: a little-endian u32 length, then 1–4096 bytes of UTF-8 JSON
/// (protocol/SPEC.md, "Control messages").
/// </summary>
public static class ControlFrame
{
    public const int HeaderSize = 4;
    public const int MaxBodySize = 4096;
    internal const int MaxDepth = 32;

    // Senders escape only what JSON requires, so a frame's size doesn't depend on the encoder. The
    // default encoder also escapes + (common in base64) and every non-ASCII character.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<string, JsonTypeInfo> MessageTypes =
        ControlJsonContext.Default.ControlMessage.PolymorphismOptions!.DerivedTypes.ToDictionary(
            derived => (string)derived.TypeDiscriminator!,
            derived => ControlJsonContext.Default.GetTypeInfo(derived.DerivedType)!);

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789ABCDEFabcdef");

    /// <summary>
    /// Finds the frame at the start of buffer. The length alone decides <see cref="FrameStatus.Empty"/>
    /// and <see cref="FrameStatus.TooLong"/>, so a reader can stop after the 4-byte header.
    /// </summary>
    public static FrameStatus TryRead(ReadOnlySpan<byte> buffer, out ReadOnlySpan<byte> body)
    {
        body = default;
        if (buffer.Length < HeaderSize)
            return FrameStatus.Incomplete;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (length == 0)
            return FrameStatus.Empty;
        if (length > MaxBodySize)
            return FrameStatus.TooLong;
        if (buffer.Length - HeaderSize < (int)length)
            return FrameStatus.Incomplete;
        body = buffer.Slice(HeaderSize, (int)length);
        return FrameStatus.Complete;
    }

    /// <summary>
    /// Decodes a frame body. False means a bad message, and fault says why. True with a null message
    /// means a type this version doesn't know, which receivers ignore.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> body, out ControlMessage? message, out ControlFault fault)
    {
        message = null;
        fault = Scan(body, out var type);
        if (fault != ControlFault.None)
            return false;
        if (!MessageTypes.TryGetValue(type!, out var typeInfo))
            return true;
        try
        {
            message = (ControlMessage?)JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException)
        {
            // A missing field, null, or a value of the wrong JSON type.
        }
        if (message is null || !IsValid(message))
        {
            message = null;
            fault = ControlFault.Field;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The frame for a message: compact JSON, <c>type</c> first, then the fields in table order.
    /// Throws <see cref="ArgumentException"/> for a message a receiver would reject, or a frame over
    /// 4096 bytes.
    /// </summary>
    public static byte[] Encode(ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsValid(message))
            throw new ArgumentException($"A receiver would reject this message: {message}", nameof(message));
        var body = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(body, WriterOptions))
            JsonSerializer.Serialize(writer, message, ControlJsonContext.Default.ControlMessage);
        if (body.WrittenCount > MaxBodySize)
            throw new ArgumentException($"The frame body is {body.WrittenCount} bytes, over {MaxBodySize}.", nameof(message));
        var frame = new byte[HeaderSize + body.WrittenCount];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.WrittenCount);
        body.WrittenSpan.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    // One pass over the whole body before any field is bound, since the rules cover fields no message
    // has: the body must be UTF-8 and JSON, with no escape for a lone surrogate, and no object may
    // repeat a name. Also finds the top-level type.
    private static ControlFault Scan(ReadOnlySpan<byte> body, out string? type)
    {
        type = null;
        if (!Utf8.IsValid(body))
            return ControlFault.Utf8;
        var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = MaxDepth });
        // The names of each open object, innermost on top. A name always belongs to the innermost
        // open object, since any array inside it has closed by then.
        var names = new Stack<HashSet<string>>();
        var typeIsNext = false;
        bool isObject;
        try
        {
            reader.Read();
            isObject = reader.TokenType == JsonTokenType.StartObject;
            do
            {
                if (typeIsNext)
                {
                    type = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    typeIsNext = false;
                }
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        names.Push([]);
                        break;
                    case JsonTokenType.EndObject:
                        names.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString()!; // throws InvalidOperationException for a lone surrogate
                        if (!names.Peek().Add(name))
                            return ControlFault.NameRepeated;
                        typeIsNext = names.Count == 1 && name == "type";
                        break;
                    case JsonTokenType.String when reader.ValueIsEscaped:
                        _ = reader.GetString(); // as above
                        break;
                }
            } while (reader.Read());
        }
        catch (JsonException)
        {
            return ControlFault.Json;
        }
        catch (InvalidOperationException)
        {
            return ControlFault.Utf8;
        }
        if (!isObject)
            return ControlFault.NotObject;
        return type is null ? ControlFault.Type : ControlFault.None;
    }

    // The field rules in protocol/SPEC.md's field table. Decoding and encoding both apply them.
    private static bool IsValid(ControlMessage message) => message switch
    {
        Hello m => m.Proto >= 1 && IsText(m.DeviceId) && IsText(m.DeviceName) && Enum.IsDefined(m.Platform)
            && IsText(m.AppVersion) && HasSize(m.Nonce, ControlMessage.NonceSize),
        PairRequired m => HasSize(m.Key, ControlMessage.KeySize) && HasSize(m.Nonce, ControlMessage.NonceSize),
        Pair m => HasSize(m.Key, ControlMessage.KeySize) && HasSize(m.Proof, ControlMessage.HmacSize),
        Challenge m => HasSize(m.Nonce, ControlMessage.NonceSize),
        Auth m => HasSize(m.Mac, ControlMessage.HmacSize),
        Welcome m => IsSlot(m.Slot) && IsColour(m.Colour) && Enum.IsDefined(m.Mode) && Enum.IsDefined(m.PadType)
            && m.UdpPort is >= 1 and <= 65535,
        ModeChanged m => Enum.IsDefined(m.Mode),
        SlotChanged m => IsSlot(m.Slot) && IsColour(m.Colour) && Enum.IsDefined(m.PadType),
        Battery m => m.Pct is >= 0 and <= 100,
        Error m => IsText(m.Code),
        Ping or Pong or Bye => true,
        _ => false,
    };

    private static bool IsSlot(int slot) => slot is >= 0 and < Packet.SlotCount;

    private static bool HasSize(byte[]? bytes, int size) => bytes?.Length == size;

    private static bool IsColour(string? colour) =>
        colour is { Length: 7 } && colour[0] == '#' && !colour.AsSpan(1).ContainsAnyExcept(HexDigits);

    // Not empty, and UTF-8 can carry it: a decoded string always can, but a string to encode may hold
    // a lone surrogate.
    private static bool IsText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i]))
                continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[i + 1]))
                return false;
            i++;
        }
        return true;
    }
}
