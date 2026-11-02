using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// Result output from a <see cref="FunctionCall"/> returned to the model.
/// See <see href="https://ai.google.dev/api/caching#FunctionResponse"/>.
/// </summary>
public sealed class FunctionResponse
{
    /// <summary>
    /// Optional. Identifier of the corresponding <see cref="FunctionCall.Id"/> when provided by the model.
    /// </summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>
    /// Required. Name of the function that was called (corresponding to <see cref="FunctionCall.Name"/>).
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>
    /// Required. Function output structured as a JSON object.
    /// </summary>
    [JsonPropertyName("response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Response { get; set; }
}
