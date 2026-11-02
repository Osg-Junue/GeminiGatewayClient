using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Configuration for model thinking/reasoning behavior on supported Gemini models.
/// <para>
/// Gemini 3 and later models use <see cref="ThinkingLevel"/>; Gemini 2.5 series models use <see cref="ThinkingBudget"/>.
/// <see cref="ThinkingLevel"/> and <see cref="ThinkingBudget"/> are mutually exclusive and cannot both be set on the same request.
/// </para>
/// See <see href="https://ai.google.dev/gemini-api/docs/thinking"/>.
/// </summary>
public sealed class ThinkingConfig
{
    /// <summary>
    /// Optional. Token budget allocated for internal thinking on Gemini 2.5 series models
    /// (<c>-1</c> for dynamic thinking, <c>0</c> to disable thinking where supported).
    /// Mutually exclusive with <see cref="ThinkingLevel"/>.
    /// </summary>
    [JsonPropertyName("thinkingBudget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ThinkingBudget { get; set; }

    /// <summary>
    /// Optional. Qualitative reasoning depth for Gemini 3 and later models.
    /// Mutually exclusive with <see cref="ThinkingBudget"/>.
    /// </summary>
    [JsonPropertyName("thinkingLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingLevel? ThinkingLevel { get; set; }

    /// <summary>
    /// Optional. Indicates whether thought summaries should be included in the response parts.
    /// </summary>
    [JsonPropertyName("includeThoughts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeThoughts { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Creates a copy of this configuration.
    /// </summary>
    internal ThinkingConfig Clone()
    {
        var clone = (ThinkingConfig)MemberwiseClone();
        if (ExtensionData is not null)
        {
            clone.ExtensionData = new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal);
        }

        return clone;
    }

    /// <summary>
    /// Validates that <see cref="ThinkingBudget"/> and <see cref="ThinkingLevel"/> are not both set.
    /// </summary>
    /// <param name="paramName">The parameter name to report in thrown <see cref="ArgumentException"/> instances.</param>
    /// <exception cref="ArgumentException">Both members are set.</exception>
    internal void Validate(string paramName = "request")
    {
        if (ThinkingBudget is not null && ThinkingLevel is not null)
        {
            throw new ArgumentException(
                "ThinkingConfig.ThinkingBudget and ThinkingConfig.ThinkingLevel are mutually exclusive and cannot both be set.",
                paramName);
        }

        if (ThinkingLevel == Models.ThinkingLevel.Unknown)
        {
            throw new ArgumentException("ThinkingConfig.ThinkingLevel cannot be ThinkingLevel.Unknown.", paramName);
        }
    }
}

/// <summary>
/// Qualitative thinking level for Gemini 3 and later models.
/// See <see href="https://ai.google.dev/gemini-api/docs/thinking"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<ThinkingLevel>))]
public enum ThinkingLevel
{
    /// <summary>
    /// Unspecified thinking level (<c>THINKING_LEVEL_UNSPECIFIED</c>); uses the model's default.
    /// </summary>
    [JsonStringEnumMemberName("THINKING_LEVEL_UNSPECIFIED")]
    Unspecified,

    /// <summary>
    /// Minimal or near-zero thinking (<c>MINIMAL</c>) for low-latency classification, extraction, or basic chat.
    /// </summary>
    [JsonStringEnumMemberName("MINIMAL")]
    Minimal,

    /// <summary>
    /// Low reasoning depth (<c>LOW</c>) for simple tasks with reduced latency and cost.
    /// </summary>
    [JsonStringEnumMemberName("LOW")]
    Low,

    /// <summary>
    /// Balanced reasoning depth (<c>MEDIUM</c>) suitable for general-purpose workloads.
    /// </summary>
    [JsonStringEnumMemberName("MEDIUM")]
    Medium,

    /// <summary>
    /// Maximum reasoning depth (<c>HIGH</c>) for complex math, coding, and multi-step planning.
    /// </summary>
    [JsonStringEnumMemberName("HIGH")]
    High,

    /// <summary>
    /// Fallback value used when the server returns a thinking level not yet defined in this version of the library.
    /// </summary>
    Unknown
}
