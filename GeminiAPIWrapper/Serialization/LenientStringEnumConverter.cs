using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Serialization;

/// <summary>
/// AOT-compatible string enum converter that honors <see cref="JsonStringEnumMemberNameAttribute"/>
/// and falls back to an <c>Unknown</c> member (when defined on <typeparamref name="TEnum"/>) instead of
/// throwing <see cref="JsonException"/> on unrecognized wire values returned by newer API versions.
/// </summary>
internal sealed class LenientStringEnumConverter<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>
    : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> s_fromWire = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<TEnum, string> s_toWire = [];
    private static readonly TEnum? s_unknown;

    static LenientStringEnumConverter()
    {
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            var wireName = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? field.Name;

            if (string.Equals(field.Name, "Unknown", StringComparison.Ordinal))
            {
                s_unknown = value;
                continue;
            }

            s_fromWire.TryAdd(wireName, value);
            s_fromWire.TryAdd(field.Name, value);
            s_toWire.TryAdd(value, wireName);
        }
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (raw is not null && s_fromWire.TryGetValue(raw, out var parsed))
            {
                return parsed;
            }

            if (s_unknown is { } unknownValue)
            {
                return unknownValue;
            }

            throw new JsonException($"Unrecognized {typeof(TEnum).Name} value '{raw}'.");
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var intVal))
        {
            var candidate = (TEnum)Enum.ToObject(typeof(TEnum), intVal);
            if (s_toWire.ContainsKey(candidate))
            {
                return candidate;
            }

            if (s_unknown is { } unknownValue)
            {
                return unknownValue;
            }
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when parsing {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (s_toWire.TryGetValue(value, out var wireName))
        {
            writer.WriteStringValue(wireName);
            return;
        }

        throw new JsonException($"Enum value '{typeof(TEnum).Name}.{value}' cannot be serialized to a Gemini API request.");
    }
}
