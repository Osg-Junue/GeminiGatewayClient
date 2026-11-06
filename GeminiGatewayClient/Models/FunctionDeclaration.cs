using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

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
    public required string Name
    {
        get;
        set => field = value ?? "";
    } = "";

    /// <summary>
    /// Optional. Brief description of what the function does, used by the model to decide when to call it.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional. OpenAPI 3.03 schema describing the parameters accepted by this function.
    /// Mutually exclusive with <see cref="ParametersJsonSchema"/>.
    /// </summary>
    [JsonPropertyName("parameters")]
    public Schema? Parameters { get; set; }

    /// <summary>
    /// Optional. Standard JSON Schema object describing the parameters accepted by this function.
    /// Preserves full JSON Schema keywords (<c>$defs</c>, <c>$ref</c>, <c>anyOf</c>, etc.) without lossy conversion.
    /// Mutually exclusive with <see cref="Parameters"/>.
    /// </summary>
    [JsonPropertyName("parametersJsonSchema")]
    public JsonElement? ParametersJsonSchema { get; set; }

    /// <summary>
    /// Optional. OpenAPI 3.03 schema describing the output returned by this function.
    /// Mutually exclusive with <see cref="ResponseJsonSchema"/>.
    /// </summary>
    [JsonPropertyName("response")]
    public Schema? Response { get; set; }

    /// <summary>
    /// Optional. Standard JSON Schema object describing the output returned by this function.
    /// Mutually exclusive with <see cref="Response"/>.
    /// </summary>
    [JsonPropertyName("responseJsonSchema")]
    public JsonElement? ResponseJsonSchema { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
