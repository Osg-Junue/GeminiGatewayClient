using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Structured representation of a function declaration exposed to the model for tool calling.
/// See <see href="https://ai.google.dev/api/caching#FunctionDeclaration"/>.
/// </summary>
public sealed class FunctionDeclaration
{
    /// <summary>
    /// Required. Name of the function. Must start with a letter or underscore and contain only
    /// alphanumeric characters, underscores, dots, or dashes, with a maximum length of 64 characters.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Optional. Brief description of what the function does, used by the model to decide when to call it.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// Optional. OpenAPI 3.03 schema describing the parameters accepted by this function.
    /// Mutually exclusive with <see cref="ParametersJsonSchema"/>.
    /// </summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Schema? Parameters { get; init; }

    /// <summary>
    /// Optional. Standard JSON Schema object describing the parameters accepted by this function.
    /// Preserves full JSON Schema keywords (<c>$defs</c>, <c>$ref</c>, <c>anyOf</c>, etc.) without lossy conversion.
    /// Mutually exclusive with <see cref="Parameters"/>.
    /// </summary>
    [JsonPropertyName("parametersJsonSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ParametersJsonSchema { get; init; }

    /// <summary>
    /// Optional. OpenAPI 3.03 schema describing the output returned by this function.
    /// Mutually exclusive with <see cref="ResponseJsonSchema"/>.
    /// </summary>
    [JsonPropertyName("response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Schema? Response { get; init; }

    /// <summary>
    /// Optional. Standard JSON Schema object describing the output returned by this function.
    /// Mutually exclusive with <see cref="Response"/>.
    /// </summary>
    [JsonPropertyName("responseJsonSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ResponseJsonSchema { get; init; }
}
