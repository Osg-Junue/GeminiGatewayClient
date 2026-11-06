using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// OpenAPI 3.03 schema object used to define structured output formats and function parameter/response types.
/// See <see href="https://ai.google.dev/api/caching#Schema"/>.
/// </summary>
public sealed class Schema
{
    /// <summary>
    /// Optional. Data type of the schema element.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SchemaType? Type { get; set; }

    /// <summary>
    /// Optional. Title of the schema element.
    /// </summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

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
    public IList<string>? Enum { get; set; }

    /// <summary>
    /// Optional. Named property schemas when <see cref="Type"/> is <see cref="SchemaType.Object"/>.
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, Schema>? Properties { get; set; }

    /// <summary>
    /// Optional. Explicit ordering of object properties when <see cref="Type"/> is <see cref="SchemaType.Object"/>.
    /// </summary>
    [JsonPropertyName("propertyOrdering")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? PropertyOrdering { get; set; }

    /// <summary>
    /// Optional. Names of required properties when <see cref="Type"/> is <see cref="SchemaType.Object"/>.
    /// </summary>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? Required { get; set; }

    /// <summary>
    /// Optional. Schema of array elements when <see cref="Type"/> is <see cref="SchemaType.Array"/>.
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Schema? Items { get; set; }

    /// <summary>
    /// Optional. Minimum number of elements when <see cref="Type"/> is <see cref="SchemaType.Array"/>.
    /// </summary>
    [JsonPropertyName("minItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MinItems { get; set; }

    /// <summary>
    /// Optional. Maximum number of elements when <see cref="Type"/> is <see cref="SchemaType.Array"/>.
    /// </summary>
    [JsonPropertyName("maxItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxItems { get; set; }

    /// <summary>
    /// Optional. Minimum numeric value when <see cref="Type"/> is <see cref="SchemaType.Number"/> or <see cref="SchemaType.Integer"/>.
    /// </summary>
    [JsonPropertyName("minimum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Minimum { get; set; }

    /// <summary>
    /// Optional. Maximum numeric value when <see cref="Type"/> is <see cref="SchemaType.Number"/> or <see cref="SchemaType.Integer"/>.
    /// </summary>
    [JsonPropertyName("maximum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Maximum { get; set; }

    /// <summary>
    /// Optional. Subschemas of which the value must match at least one.
    /// </summary>
    [JsonPropertyName("anyOf")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<Schema>? AnyOf { get; set; }

    /// <summary>
    /// Optional. Default value of the field.
    /// </summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Default { get; set; }

    /// <summary>
    /// Additional OpenAPI / JSON Schema keywords returned or accepted by the endpoint.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
