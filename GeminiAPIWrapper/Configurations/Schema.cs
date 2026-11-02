using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// OpenAPI 3.03 schema object used to define structured output formats and function parameter/response types.
/// See <see href="https://ai.google.dev/api/caching#Schema"/>.
/// </summary>
public sealed class Schema
{
    /// <summary>
    /// Required. Data type of the schema element.
    /// </summary>
    [JsonPropertyName("type")]
    public SchemaType Type { get; set; }

    /// <summary>
    /// Optional. Human- and model-readable description of the value represented by this schema.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional. Data format modifier (for example <c>date-time</c>, <c>int64</c>, <c>double</c>, <c>enum</c>).
    /// </summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }

    /// <summary>
    /// Optional. Indicates whether the value may be <c>null</c>.
    /// </summary>
    [JsonPropertyName("nullable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Nullable { get; set; }

    /// <summary>
    /// Optional. Allowed string values when <see cref="Type"/> is <see cref="SchemaType.String"/>.
    /// </summary>
    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Enum { get; set; }

    /// <summary>
    /// Optional. Named property schemas when <see cref="Type"/> is <see cref="SchemaType.Object"/>.
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Schema>? Properties { get; set; }

    /// <summary>
    /// Optional. Names of required properties when <see cref="Type"/> is <see cref="SchemaType.Object"/>.
    /// </summary>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Required { get; set; }

    /// <summary>
    /// Optional. Schema of array elements when <see cref="Type"/> is <see cref="SchemaType.Array"/>.
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Schema? Items { get; set; }
}
