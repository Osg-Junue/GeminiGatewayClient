using System.Text.Json.Serialization;
using GeminiAPIWrapper.Serialization;

namespace GeminiAPIWrapper.Configurations;

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
    High
}
