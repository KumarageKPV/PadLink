using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadLink.Protocol.Control;

// Strict by design: a null or missing field throws instead of binding null or 0. Unknown fields are
// skipped, as SPEC.md asks.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    MaxDepth = ControlFrame.MaxDepth,
    Converters = [typeof(Base64Converter)])]
[JsonSerializable(typeof(ControlMessage))]
[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(PairRequired))]
[JsonSerializable(typeof(Pair))]
[JsonSerializable(typeof(Challenge))]
[JsonSerializable(typeof(Auth))]
[JsonSerializable(typeof(Welcome))]
[JsonSerializable(typeof(ModeChanged))]
[JsonSerializable(typeof(SlotChanged))]
[JsonSerializable(typeof(Ping))]
[JsonSerializable(typeof(Pong))]
[JsonSerializable(typeof(Battery))]
[JsonSerializable(typeof(Bye))]
[JsonSerializable(typeof(Error))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;

/// <summary>
/// Standard base64 with padding and nothing else (protocol/SPEC.md, "Conventions"). .NET's own
/// decoder also accepts spaces and line breaks, and ignores the unused bits before the padding, so
/// the text is checked first and the bytes must encode back to it.
/// </summary>
internal sealed class Base64Converter : JsonConverter<byte[]>
{
    private static readonly SearchValues<char> Alphabet =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");

    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a base64 string.");
        var text = reader.GetString()!;
        if (!IsStrict(text))
            throw new JsonException("Expected standard base64 with padding.");
        var bytes = Convert.FromBase64String(text);
        if (Convert.ToBase64String(bytes) != text)
            throw new JsonException("Expected the unused bits before the padding to be zero.");
        return bytes;
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        writer.WriteBase64StringValue(value);

    private static bool IsStrict(string text)
    {
        var padding = text.EndsWith("==", StringComparison.Ordinal) ? 2 : text.EndsWith('=') ? 1 : 0;
        return text.Length % 4 == 0 && !text.AsSpan(0, text.Length - padding).ContainsAnyExcept(Alphabet);
    }
}

/// <summary>
/// Reads and writes an enum as one of its wire names, exactly. System.Text.Json's own enum converter
/// ignores case and accepts numbers.
/// </summary>
internal abstract class WireNameConverter<T>(params (T Value, string Name)[] names) : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            foreach (var (value, name) in names)
            {
                if (reader.ValueTextEquals(name))
                    return value;
            }
        }
        throw new JsonException($"Expected a {typeof(T).Name} name.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        foreach (var (candidate, name) in names)
        {
            if (EqualityComparer<T>.Default.Equals(candidate, value))
            {
                writer.WriteStringValue(name);
                return;
            }
        }
        throw new JsonException($"{typeof(T).Name} {value} has no wire name.");
    }
}
