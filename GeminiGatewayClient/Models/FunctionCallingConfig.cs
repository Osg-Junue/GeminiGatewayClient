using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Configuration specifying how the model should use declared functions.
/// See <see href="https://ai.google.dev/api/caching#FunctionCallingConfig"/>.
/// </summary>
public sealed class FunctionCallingConfig
{
    /// <summary>
    /// Execution mode for function calling. Defaults to <see cref="FunctionCallingMode.Auto"/>.
    /// </summary>
    [JsonPropertyName("mode")]
    public FunctionCallingMode Mode { get; set; } = FunctionCallingMode.Auto;

    /// <summary>
    /// Optional. Set of function names the model is allowed to call.
    /// Only applicable when <see cref="Mode"/> is <see cref="FunctionCallingMode.Any"/> or <see cref="FunctionCallingMode.Validated"/>.
    /// </summary>
    [JsonPropertyName("allowedFunctionNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? AllowedFunctionNames { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    internal FunctionCallingConfig Clone()
    {
        var clone = (FunctionCallingConfig)MemberwiseClone();
        if (AllowedFunctionNames is not null)
        {
            clone.AllowedFunctionNames = [.. AllowedFunctionNames];
        }

        if (ExtensionData is not null)
        {
            clone.ExtensionData = new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal);
        }

        return clone;
    }
}

/// <summary>
/// Defines the execution behavior for function calling.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<FunctionCallingMode>))]
public enum FunctionCallingMode
{
    /// <summary>
    /// The model decides whether to predict a function call or a natural language response.
    /// </summary>
    [JsonStringEnumMemberName("AUTO")]
    Auto,

    /// <summary>
    /// The model is prohibited from making function calls.
    /// </summary>
    [JsonStringEnumMemberName("NONE")]
    None,

    /// <summary>
    /// The model is constrained to always predict a function call from the allowed functions.
    /// </summary>
    [JsonStringEnumMemberName("ANY")]
    Any,

    /// <summary>
    /// The model predicts either a function call or a natural language response, enforcing schema adherence on function calls.
    /// </summary>
    [JsonStringEnumMemberName("VALIDATED")]
    Validated,

    /// <summary>
    /// Fallback value used when the server returns a function calling mode not yet defined in this version of the library.
    /// </summary>
    Unknown
}
