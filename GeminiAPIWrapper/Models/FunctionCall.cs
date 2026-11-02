using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// A predicted function call returned by the model containing the function name and its structured arguments.
/// See <see href="https://ai.google.dev/api/caching#FunctionCall"/>.
/// </summary>
public sealed class FunctionCall
{
    /// <summary>
    /// Optional. Unique identifier of the function call. When populated by the model, pass the same ID in the
    /// corresponding <see cref="FunctionResponse.Id"/> so parallel calls to the same function can be correlated.
    /// </summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>
    /// Required. Name of the function to call (matches a <see cref="FunctionDeclaration.Name"/>).
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>
    /// Optional. Function parameters and values represented as a JSON object.
    /// </summary>
    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Args { get; set; }
}
