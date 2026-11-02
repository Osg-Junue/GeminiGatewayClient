using System.Text.Json.Serialization;
using GeminiAPIWrapper.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// OpenAPI data types supported by <see cref="Schema"/>.
/// See <see href="https://ai.google.dev/api/caching#Type"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<SchemaType>))]
public enum SchemaType
{
    /// <summary>
    /// String type. Also supports case-insensitive <c>string</c> when parsing standard JSON Schema.
    /// </summary>
    [JsonStringEnumMemberName("STRING")]
    String,

    /// <summary>
    /// Floating-point or general numeric type.
    /// </summary>
    [JsonStringEnumMemberName("NUMBER")]
    Number,

    /// <summary>
    /// Integer numeric type.
    /// </summary>
    [JsonStringEnumMemberName("INTEGER")]
    Integer,

    /// <summary>
    /// Boolean type.
    /// </summary>
    [JsonStringEnumMemberName("BOOLEAN")]
    Boolean,

    /// <summary>
    /// Array / list type.
    /// </summary>
    [JsonStringEnumMemberName("ARRAY")]
    Array,

    /// <summary>
    /// Key-value object / map type.
    /// </summary>
    [JsonStringEnumMemberName("OBJECT")]
    Object
}
