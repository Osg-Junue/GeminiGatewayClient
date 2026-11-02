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

    internal ToolConfig Clone()
    {
        var clone = (ToolConfig)MemberwiseClone();
        clone.FunctionCallingConfig = FunctionCallingConfig?.Clone();
        if (ExtensionData is not null)
        {
            clone.ExtensionData = new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal);
        }

        return clone;
    }

    internal void Validate(string paramName = "request")
    {
        if (FunctionCallingConfig?.Mode == FunctionCallingMode.Unknown)
        {
            throw new ArgumentException("FunctionCallingConfig.Mode cannot be FunctionCallingMode.Unknown.", paramName);
        }
    }
}
