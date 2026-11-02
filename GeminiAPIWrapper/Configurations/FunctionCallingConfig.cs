using System.Text.Json.Serialization;
using GeminiAPIWrapper.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Configuration specifying how the model should use declared functions.
/// See <see href="https://ai.google.dev/api/caching#FunctionCallingConfig"/>.
/// </summary>
public sealed class FunctionCallingConfig
{
    /// <summary>
    /// Execution mode for function calling. Defaults to <see cref="FunctionCallingMode.AUTO"/>.
    /// </summary>
    [JsonPropertyName("mode")]
    public FunctionCallingMode Mode { get; init; } = FunctionCallingMode.AUTO;

    /// <summary>
    /// Optional. Set of function names the model is allowed to call.
    /// Only applicable when <see cref="Mode"/> is <see cref="FunctionCallingMode.ANY"/> or <see cref="FunctionCallingMode.VALIDATED"/>.
    /// </summary>
    [JsonPropertyName("allowedFunctionNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedFunctionNames { get; init; }
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
    AUTO,

    /// <summary>
    /// The model is prohibited from making function calls.
    /// </summary>
    [JsonStringEnumMemberName("NONE")]
    NONE,

    /// <summary>
    /// The model is constrained to always predict a function call from the allowed functions.
    /// </summary>
    [JsonStringEnumMemberName("ANY")]
    ANY,

    /// <summary>
    /// The model predicts either a function call or a natural language response, enforcing schema adherence on function calls.
    /// </summary>
    [JsonStringEnumMemberName("VALIDATED")]
    VALIDATED,
}
