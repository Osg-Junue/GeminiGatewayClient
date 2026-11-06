using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Serialization;

/// <summary>
/// Serializes an enum using the wire value declared via <see cref="JsonStringEnumMemberNameAttribute"/>
/// and falls back to the <c>Unknown</c> member (when defined on <typeparamref name="TEnum"/>) or
/// <c>default</c> when the server returns an unrecognized string or integer value.
/// </summary>
/// <typeparam name="TEnum">The enum type handled by the converter.</typeparam>
public sealed class LenientStringEnumConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, JsonEncodedText> s_toWire;
    private static readonly Dictionary<string, TEnum> s_fromWire;
    private static readonly Dictionary<int, TEnum> s_fromNumeric;
    private static readonly TEnum s_unknownValue;

    static LenientStringEnumConverter()
    {
        var toWire = new Dictionary<TEnum, JsonEncodedText>();
        var fromWire = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);
        var fromNumeric = new Dictionary<int, TEnum>();
        TEnum unknown = default;

        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            var name = field.Name;
            var wireAttr = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
            var wire = wireAttr?.Name ?? name;

            if (string.Equals(name, "Unknown", StringComparison.Ordinal))
            {
                unknown = value;
                var unknownWire = wireAttr?.Name ?? "UNKNOWN";
                toWire[value] = JsonEncodedText.Encode(unknownWire);
                fromWire.TryAdd(unknownWire, value);
                fromWire.TryAdd(name, value);
                continue;
            }

            var numericValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            fromNumeric.TryAdd(numericValue, value);

            toWire[value] = JsonEncodedText.Encode(wire);
            fromWire[wire] = value;
            fromWire.TryAdd(name, value);
        }

        s_toWire = toWire;
        s_fromWire = fromWire;
        s_fromNumeric = fromNumeric;
        s_unknownValue = unknown;
    }

    /// <inheritdoc />
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() is { } value && s_fromWire.TryGetValue(value, out var parsed) ? parsed : s_unknownValue;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
        {
            return s_fromNumeric.TryGetValue(numeric, out var candidate) ? candidate : s_unknownValue;
        }

        reader.Skip();
        return s_unknownValue;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (s_toWire.TryGetValue(value, out var wire))
        {
            writer.WriteStringValue(wire);
            return;
        }

        writer.WriteStringValue(value.ToString());
    }
}
