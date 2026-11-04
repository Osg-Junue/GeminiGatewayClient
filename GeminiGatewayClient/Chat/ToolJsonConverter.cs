using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeminiGatewayClient.Http;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Converts function-call arguments and function results between <c>Microsoft.Extensions.AI</c> object graphs and the
/// JSON objects required by Gemini, without relying on reflection for common primitive and collection types.
/// </summary>
internal static class ToolJsonConverter
{
    private const string ResultPropertyName = "result";

    /// <summary>
    /// Converts function-call arguments to a JSON object, or <c>null</c> when there are none.
    /// </summary>
    public static JsonElement? ToArguments(IDictionary<string, object?>? arguments, JsonSerializerOptions options)
    {
        if (arguments is null)
        {
            return null;
        }

        using var buffer = new PooledByteBufferWriter(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteObject(writer, arguments, options);
        }

        return ParseBuffer(buffer.WrittenSpan);
    }

    /// <summary>
    /// Converts an arbitrary value into a <see cref="JsonElement"/> without relying on reflection for common primitive,
    /// dictionary, and collection types.
    /// </summary>
    public static JsonElement ToJsonElement(object? value, JsonSerializerOptions options)
    {
        if (value is JsonElement element)
        {
            return element.Clone();
        }

        using var buffer = new PooledByteBufferWriter(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteValue(writer, value, options);
        }

        return ParseBuffer(buffer.WrittenSpan);
    }

    /// <summary>
    /// Converts a function result to the JSON object required by <c>functionResponse.response</c>.
    /// Non-object results are wrapped as <c>{"result": value}</c>.
    /// </summary>
    public static JsonElement ToResponseObject(object? result, JsonSerializerOptions options)
    {
        if (result is JsonObject jsonObject)
        {
            using var nodeBuffer = new PooledByteBufferWriter(256);
            using (var writer = new Utf8JsonWriter(nodeBuffer))
            {
                jsonObject.WriteTo(writer);
            }

            return ParseBuffer(nodeBuffer.WrittenSpan);
        }

        if (result is JsonElement { ValueKind: JsonValueKind.Object } objectElement)
        {
            return objectElement.Clone();
        }

        if (TryWriteKeyValuePairsToElement(result, options, out var dictElement))
        {
            return dictElement;
        }

        if (result is not null and not JsonElement and not JsonNode and not string &&
            TrySerialize(result, options, out var serialized) &&
            serialized.ValueKind == JsonValueKind.Object)
        {
            return serialized;
        }

        using var wrappedBuffer = new PooledByteBufferWriter(256);
        using (var writer = new Utf8JsonWriter(wrappedBuffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(ResultPropertyName);
            WriteValue(writer, result, options);
            writer.WriteEndObject();
        }

        return ParseBuffer(wrappedBuffer.WrittenSpan);
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

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static bool TryWriteKeyValuePairsToElement(object? value, JsonSerializerOptions options, out JsonElement element)
    {
        if (!IsKeyValueSequenceOrDictionary(value))
        {
            element = default;
            return false;
        }

        using var dictBuffer = new PooledByteBufferWriter(256);
        using (var writer = new Utf8JsonWriter(dictBuffer))
        {
            TryWriteKeyValuePairs(writer, value, options);
        }

        element = ParseBuffer(dictBuffer.WrittenSpan);
        return true;
    }

    private static bool IsKeyValueSequenceOrDictionary(object? value) =>
        value is IEnumerable<KeyValuePair<string, object?>>
            or IEnumerable<KeyValuePair<string, string?>>
            or IEnumerable<KeyValuePair<string, int>>
            or IEnumerable<KeyValuePair<string, long>>
            or IEnumerable<KeyValuePair<string, double>>
            or IEnumerable<KeyValuePair<string, float>>
            or IEnumerable<KeyValuePair<string, decimal>>
            or IEnumerable<KeyValuePair<string, bool>>
            or IEnumerable<KeyValuePair<string, byte>>
            or IEnumerable<KeyValuePair<string, sbyte>>
            or IEnumerable<KeyValuePair<string, short>>
            or IEnumerable<KeyValuePair<string, ushort>>
            or IEnumerable<KeyValuePair<string, uint>>
            or IEnumerable<KeyValuePair<string, ulong>>
            or IEnumerable<KeyValuePair<string, Guid>>
            or IEnumerable<KeyValuePair<string, DateTime>>
            or IEnumerable<KeyValuePair<string, DateTimeOffset>>
            or IEnumerable<KeyValuePair<string, DateOnly>>
            or IEnumerable<KeyValuePair<string, TimeOnly>>
            or IEnumerable<KeyValuePair<string, TimeSpan>>
            or IEnumerable<KeyValuePair<string, Uri?>>
            or IEnumerable<KeyValuePair<string, JsonElement>>
            or IEnumerable<KeyValuePair<string, JsonNode?>>
            or IDictionary;

    private static bool TryWriteKeyValuePairs(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case IEnumerable<KeyValuePair<string, object?>> pairs: WriteObject(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, string?>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, int>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, long>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, double>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, float>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, decimal>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, bool>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, byte>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, sbyte>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, short>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, ushort>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, uint>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, ulong>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, Guid>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, DateTime>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, DateTimeOffset>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, DateOnly>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, TimeOnly>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, TimeSpan>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, Uri?>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, JsonElement>> pairs: WritePairs(writer, pairs, options); return true;
            case IEnumerable<KeyValuePair<string, JsonNode?>> pairs: WritePairs(writer, pairs, options); return true;
            case IDictionary dictionary: WriteDictionary(writer, dictionary, options); return true;
            default: return false;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> properties, JsonSerializerOptions options) =>
        WritePairs(writer, properties, options);

    private static void WritePairs<TValue>(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, TValue>> properties, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in properties)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value, options);
        }

        writer.WriteEndObject();
    }

    private static void WriteDictionary(Utf8JsonWriter writer, IDictionary dictionary, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not null)
            {
                writer.WritePropertyName(entry.Key as string ?? Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                WriteValue(writer, entry.Value, options);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case JsonElement element: element.WriteTo(writer); break;
            case JsonNode node: node.WriteTo(writer); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case byte u8: writer.WriteNumberValue(u8); break;
            case sbyte i8: writer.WriteNumberValue(i8); break;
            case short i16: writer.WriteNumberValue(i16); break;
            case ushort u16: writer.WriteNumberValue(u16); break;
            case int i32: writer.WriteNumberValue(i32); break;
            case uint u32: writer.WriteNumberValue(u32); break;
            case long i64: writer.WriteNumberValue(i64); break;
            case ulong u64: writer.WriteNumberValue(u64); break;
            case float f: writer.WriteNumberValue(f); break;
            case double d: writer.WriteNumberValue(d); break;
            case decimal m: writer.WriteNumberValue(m); break;
            case Guid guid: writer.WriteStringValue(guid); break;
            case DateTime dateTime: writer.WriteStringValue(dateTime); break;
            case DateTimeOffset dateTimeOffset: writer.WriteStringValue(dateTimeOffset); break;
            case DateOnly date: writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeOnly time: writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeSpan span: writer.WriteStringValue(span.ToString("c", CultureInfo.InvariantCulture)); break;
            case Uri uri: writer.WriteStringValue(uri.ToString()); break;
            case Enum enumValue:
                if (TrySerialize(enumValue, options, out var serializedEnum))
                {
                    serializedEnum.WriteTo(writer);
                }
                else
                {
                    writer.WriteStringValue(enumValue.ToString());
                }

                break;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
            case ReadOnlyMemory<byte> memory: writer.WriteBase64StringValue(memory.Span); break;
            case not null when TryWriteKeyValuePairs(writer, value, options): break;
            case IEnumerable sequence:
                if (TrySerialize(value, options, out var serializedObject) && serializedObject.ValueKind == JsonValueKind.Object)
                {
                    serializedObject.WriteTo(writer);
                    break;
                }

                writer.WriteStartArray();
                foreach (var item in sequence)
                {
                    WriteValue(writer, item, options);
                }

                writer.WriteEndArray();
                break;

            default:
                if (!TrySerialize(value, options, out var serialized))
                {
                    throw new InvalidOperationException(
                        $"Cannot serialize tool value of type '{value.GetType().FullName}' without JSON type metadata. " +
                        "Pass JsonSerializerOptions configured with your JsonSerializerContext to AsIChatClient(toolSerializerOptions: ...).");
                }

                serialized.WriteTo(writer);
                break;
        }
    }

    private static bool TrySerialize(object value, JsonSerializerOptions options, out JsonElement element)
    {
        if (options.TypeInfoResolver is not null && options.TryGetTypeInfo(value.GetType(), out var typeInfo))
        {
            element = JsonSerializer.SerializeToElement(value, typeInfo);
            return true;
        }

        element = default;
        return false;
    }

    private static JsonElement ParseBuffer(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        return JsonElement.ParseValue(ref reader);
    }
}
