using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Configuration for how the model uses the tools provided in <see cref="GeminiRequest.Tools"/>.
/// See <see href="https://ai.google.dev/api/caching#ToolConfig"/>.
/// </summary>
public sealed class ToolConfig
{
    /// <summary>
    /// Optional. Configuration for function calling behavior.
    /// </summary>
    [JsonPropertyName("functionCallingConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionCallingConfig? FunctionCallingConfig { get; init; }
}
