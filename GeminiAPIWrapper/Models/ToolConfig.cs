using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// Configuration for how the model uses the tools provided in <see cref="GenerateContentRequest.Tools"/>.
/// See <see href="https://ai.google.dev/api/caching#ToolConfig"/>.
/// </summary>
public sealed class ToolConfig
{
    /// <summary>
    /// Optional. Configuration for function calling behavior.
    /// </summary>
    [JsonPropertyName("functionCallingConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionCallingConfig? FunctionCallingConfig { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
