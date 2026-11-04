using System.Text.Json.Serialization;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// OpenAPI data types supported by <see cref="Schema"/>.
/// See <see href="https://ai.google.dev/api/caching#Type"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<SchemaType>))]
public enum SchemaType
{
    /// <summary>
    /// Unspecified schema data type (<c>TYPE_UNSPECIFIED</c>).
    /// </summary>
    [JsonStringEnumMemberName("TYPE_UNSPECIFIED")]
    Unspecified = 0,

    /// <summary>
    /// String type. Also supports case-insensitive <c>string</c> when parsing standard JSON Schema.
    /// </summary>
    [JsonStringEnumMemberName("STRING")]
    String = 1,

    /// <summary>
    /// Floating-point or general numeric type.
    /// </summary>
    [JsonStringEnumMemberName("NUMBER")]
    Number = 2,

    /// <summary>
    /// Integer numeric type.
    /// </summary>
    [JsonStringEnumMemberName("INTEGER")]
    Integer = 3,

    /// <summary>
    /// Boolean type.
    /// </summary>
    [JsonStringEnumMemberName("BOOLEAN")]
    Boolean = 4,

    /// <summary>
    /// Array / list type.
    /// </summary>
    [JsonStringEnumMemberName("ARRAY")]
    Array = 5,

    /// <summary>
    /// Key-value object / map type.
    /// </summary>
    [JsonStringEnumMemberName("OBJECT")]
    Object = 6,

    /// <summary>
    /// Fallback value used when the server returns a schema type not yet defined in this version of the library.
    /// </summary>
    Unknown = -1
}
