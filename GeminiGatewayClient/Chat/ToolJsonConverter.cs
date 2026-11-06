using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Converts function-call arguments and function results between <c>Microsoft.Extensions.AI</c> object graphs and the
/// JSON objects required by Gemini. Dictionaries and sequences are walked element by element, because under Native AOT
/// metadata for a collection says nothing about what it holds at run time. Everything else goes to System.Text.Json, so
/// custom converters and options apply to each value.
/// </summary>
internal static class ToolJsonConverter
{
    private const string ResultPropertyName = "result";

    /// <summary>
    /// Converts function-call arguments to a JSON object, or <c>null</c> when there are none.
    /// </summary>
    public static JsonElement? ToArguments(IDictionary<string, object?>? arguments, JsonSerializerOptions options) =>
        arguments is null ? null : ToElement(writer => WriteObject(writer, arguments, options));

    /// <summary>
    /// Converts an arbitrary value into a <see cref="JsonElement"/>.
    /// </summary>
    public static JsonElement ToJsonElement(object? value, JsonSerializerOptions options) =>
        ToElement(writer => WriteValue(writer, value, options));

    /// <summary>
    /// Converts a function result to the JSON object required by <c>functionResponse.response</c>.
    /// Non-object results are wrapped as <c>{"result": value}</c>.
    /// </summary>
    public static JsonElement ToResponseObject(object? result, JsonSerializerOptions options)
    {
        var element = ToJsonElement(result, options);
        return element.ValueKind == JsonValueKind.Object
            ? element
            : ToElement(writer => WriteObject(writer, [new(ResultPropertyName, element)], options));
    }

    /// <summary>
    /// Converts function-call arguments returned by the model into a dictionary whose values are <see cref="JsonElement"/> instances.
    /// </summary>
    public static IDictionary<string, object?>? ToDictionary(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(element.GetPropertyCount(), StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static JsonElement ToElement(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref reader);
    }

    /// <summary>
    /// Writes keys verbatim: they are parameter names, which a naming policy must not change.
    /// </summary>
    private static void WriteObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> properties, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in properties)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value, options);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            case JsonElement element:
                element.WriteTo(writer);
                break;

            case JsonNode node:
                node.WriteTo(writer);
                break;

            case not null when TrySerialize(writer, value, options) || TrySerialize(writer, value, ToolScalarContext.Default.Options):
                break;

            case Enum enumValue:
                writer.WriteStringValue(enumValue.ToString());
                break;

            case IEnumerable<KeyValuePair<string, object?>> pairs:
                WriteObject(writer, pairs, options);
                break;

            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                    WriteValue(writer, entry.Value, options);
                }

                writer.WriteEndObject();
                break;

            // A read-only dictionary of value types cannot be walked without reflection, but its own metadata can write it.
            case not null when TrySerializeDictionary(writer, value, options):
                break;

            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence)
                {
                    WriteValue(writer, item, options);
                }

                writer.WriteEndArray();
                break;

            default:
                throw new InvalidOperationException(
                    $"Cannot serialize tool value of type '{value.GetType().FullName}' without JSON type metadata. " +
                    "Pass JsonSerializerOptions configured with your JsonSerializerContext to AsIChatClient(toolSerializerOptions: ...).");
        }
    }

    private static bool TrySerialize(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is null ||
            !options.TryGetTypeInfo(value.GetType(), out var typeInfo) ||
            (typeInfo.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary && options.ReferenceHandler is null))
        {
            return false;
        }

        JsonSerializer.Serialize(writer, value, typeInfo);
        return true;
    }

    private static bool TrySerializeDictionary(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is null ||
            !options.TryGetTypeInfo(value.GetType(), out var typeInfo) ||
            typeInfo.Kind != JsonTypeInfoKind.Dictionary)
        {
            return false;
        }

        JsonSerializer.Serialize(writer, value, typeInfo);
        return true;
    }
}

/// <summary>
/// Metadata for the plain values a tool may return, available even when the caller's options do not describe them.
/// </summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(Uri))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(ReadOnlyMemory<byte>))]
internal sealed partial class ToolScalarContext : JsonSerializerContext;
