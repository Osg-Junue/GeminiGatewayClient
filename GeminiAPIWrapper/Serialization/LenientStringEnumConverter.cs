using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Serialization;

/// <summary>
/// Serializes an enum using the wire value declared via <see cref="JsonStringEnumMemberNameAttribute"/>
/// and falls back to the <c>Unknown</c> member (when defined on <typeparamref name="TEnum"/>) or
/// <c>default</c> when the server returns an unrecognized string or integer value.
/// </summary>
/// <typeparam name="TEnum">The enum type handled by the converter.</typeparam>
public sealed class LenientStringEnumConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<TEnum, string> s_toWire;
    private static readonly FrozenDictionary<string, TEnum> s_fromWire;
    private static readonly TEnum s_unknownValue;

    static LenientStringEnumConverter()
    {
        var toWire = new Dictionary<TEnum, string>();
        var fromWire = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);
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
                toWire[value] = unknownWire;
                fromWire.TryAdd(unknownWire, value);
                fromWire.TryAdd(name, value);
                continue;
            }

            toWire[value] = wire;
            fromWire[wire] = value;
            fromWire.TryAdd(name, value);
        }

        s_toWire = toWire.ToFrozenDictionary();
        s_fromWire = fromWire.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        s_unknownValue = unknown;
    }

    /// <inheritdoc />
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var length = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
            if (length <= 128)
            {
                Span<char> charBuffer = stackalloc char[128];
                var written = reader.CopyString(charBuffer);
                var span = charBuffer[..written];
                if (span.IsEmpty)
                {
                    return s_unknownValue;
                }

                var lookup = s_fromWire.GetAlternateLookup<ReadOnlySpan<char>>();
                return lookup.TryGetValue(span, out var fastParsed) ? fastParsed : s_unknownValue;
            }

            var raw = reader.GetString();
            if (string.IsNullOrEmpty(raw))
            {
                return s_unknownValue;
            }

            return s_fromWire.TryGetValue(raw, out var parsed) ? parsed : s_unknownValue;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric))
        {
            var candidate = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            return s_toWire.ContainsKey(candidate) ? candidate : s_unknownValue;
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
