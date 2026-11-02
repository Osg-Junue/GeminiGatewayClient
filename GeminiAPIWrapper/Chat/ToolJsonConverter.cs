using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GeminiAPIWrapper.Chat;

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
    public static JsonElement? ToArguments(IDictionary<string, object?>? arguments, JsonSerializerOptions options) =>
        arguments is null ? null : WriteElement(writer => WriteObject(writer, arguments, options));

    /// <summary>
    /// Converts a function result to the JSON object required by <c>functionResponse.response</c>.
    /// Non-object results are wrapped as <c>{"result": value}</c>.
    /// </summary>
    public static JsonElement ToResponseObject(object? result, JsonSerializerOptions options)
    {
        if (result is JsonNode node)
        {
            result = WriteElement(writer => node.WriteTo(writer));
        }

        switch (result)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                return element.Clone();

            case IEnumerable<KeyValuePair<string, object?>> dictionary:
                return WriteElement(writer => WriteObject(writer, dictionary, options));

            case not null and not JsonElement and not string and not IEnumerable
                when TrySerialize(result, options, out var serialized) && serialized.ValueKind == JsonValueKind.Object:
                return serialized;

            default:
                return WriteElement(writer =>
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName(ResultPropertyName);
                    WriteValue(writer, result, options);
                    writer.WriteEndObject();
                });
        }
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
            case Enum enumValue: writer.WriteStringValue(enumValue.ToString()); break;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
            case ReadOnlyMemory<byte> memory: writer.WriteBase64StringValue(memory.Span); break;
            case IEnumerable<KeyValuePair<string, object?>> dictionary: WriteObject(writer, dictionary, options); break;
            case IEnumerable sequence:
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
        if (options.TryGetTypeInfo(value.GetType(), out var typeInfo))
        {
            element = JsonSerializer.SerializeToElement(value, typeInfo);
            return true;
        }

        element = default;
        return false;
    }

    private static JsonElement WriteElement(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref reader);
    }
}
