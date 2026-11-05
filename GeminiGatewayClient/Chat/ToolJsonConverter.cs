using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Converts function-call arguments and function results between <c>Microsoft.Extensions.AI</c> object graphs and the
/// JSON objects required by Gemini. System.Text.Json does the work whenever type metadata exists, so custom converters
/// and naming policies apply. Plain dictionaries and sequences without metadata, which are common under Native AOT,
/// are written structurally.
/// </summary>
internal static class ToolJsonConverter
{
    private const string ResultPropertyName = "result";

    /// <summary>
    /// Converts function-call arguments to a JSON object, or <c>null</c> when there are none.
    /// </summary>
    public static JsonElement? ToArguments(IDictionary<string, object?>? arguments, JsonSerializerOptions options) =>
        arguments is null ? null : ToJsonElement(arguments, options);

    /// <summary>
    /// Converts an arbitrary value into a <see cref="JsonElement"/>.
    /// </summary>
    public static JsonElement ToJsonElement(object? value, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteValue(writer, value, options);
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref reader);
    }

    /// <summary>
    /// Converts a function result to the JSON object required by <c>functionResponse.response</c>.
    /// Non-object results are wrapped as <c>{"result": value}</c>.
    /// </summary>
    public static JsonElement ToResponseObject(object? result, JsonSerializerOptions options)
    {
        var element = ToJsonElement(result, options);
        return element.ValueKind == JsonValueKind.Object
            ? element
            : ToJsonElement(new Dictionary<string, object?> { [ResultPropertyName] = element }, options);
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

            case not null when TrySerialize(writer, value, options):
                break;

            case not null when IsScalar(value) && TrySerialize(writer, value, AIJsonUtilities.DefaultOptions):
                break;

            case Enum enumValue:
                writer.WriteStringValue(enumValue.ToString());
                break;

            case IEnumerable<KeyValuePair<string, object?>> pairs:
                writer.WriteStartObject();
                foreach (var (key, item) in pairs)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item, options);
                }

                writer.WriteEndObject();
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

    private static bool IsScalar(object value) =>
        value is string or bool or decimal or Guid or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or Uri or byte[] ||
        value.GetType().IsPrimitive;

    private static bool TrySerialize(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is null || !options.TryGetTypeInfo(value.GetType(), out var typeInfo))
        {
            return false;
        }

        JsonSerializer.Serialize(writer, value, typeInfo);
        return true;
    }
}
