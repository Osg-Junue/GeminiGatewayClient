using System.Text.Json.Serialization;
using GeminiAPIWrapper.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// Safety setting affecting the safety-blocking behavior for a specific <see cref="HarmCategory"/>.
/// See <see href="https://ai.google.dev/api/generate-content#safetysetting"/>.
/// </summary>
public sealed class SafetySetting
{
    /// <summary>
    /// Harm category controlled by this safety setting.
    /// </summary>
    [JsonPropertyName("category")]
    public required HarmCategory Category { get; set; }

    /// <summary>
    /// Blocking threshold applied to <see cref="Category"/>.
    /// </summary>
    [JsonPropertyName("threshold")]
    public required HarmBlockThreshold Threshold { get; set; }

    /// <summary>
    /// Optional. Method used to evaluate the threshold (probability or severity, where supported).
    /// </summary>
    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HarmBlockMethod? Method { get; set; }
}

/// <summary>
/// Threshold above which content in a <see cref="HarmCategory"/> is blocked.
/// See <see href="https://ai.google.dev/api/generate-content#harmblockthreshold"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<HarmBlockThreshold>))]
public enum HarmBlockThreshold
{
    /// <summary>
    /// Threshold is unspecified.
    /// </summary>
    [JsonStringEnumMemberName("HARM_BLOCK_THRESHOLD_UNSPECIFIED")]
    Unspecified,

    /// <summary>
    /// Content is allowed regardless of probability.
    /// </summary>
    [JsonStringEnumMemberName("BLOCK_NONE")]
    BlockNone,

    /// <summary>
    /// Block content when the probability of unsafe content is low, medium, or high.
    /// </summary>
    [JsonStringEnumMemberName("BLOCK_LOW_AND_ABOVE")]
    BlockLowAndAbove,

    /// <summary>
    /// Block content when the probability of unsafe content is medium or high.
    /// </summary>
    [JsonStringEnumMemberName("BLOCK_MEDIUM_AND_ABOVE")]
    BlockMediumAndAbove,

    /// <summary>
    /// Block content only when the probability of unsafe content is high.
    /// </summary>
    [JsonStringEnumMemberName("BLOCK_ONLY_HIGH")]
    BlockOnlyHigh,

    /// <summary>
    /// Turn off the safety filter for this category.
    /// </summary>
    [JsonStringEnumMemberName("OFF")]
    Off,

    /// <summary>
    /// Fallback value used when the server returns a threshold not yet defined in this version of the library.
    /// </summary>
    Unknown
}

/// <summary>
/// Method used to evaluate whether a safety threshold is exceeded.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<HarmBlockMethod>))]
public enum HarmBlockMethod
{
    /// <summary>
    /// Evaluation method is unspecified.
    /// </summary>
    [JsonStringEnumMemberName("HARM_BLOCK_METHOD_UNSPECIFIED")]
    Unspecified,

    /// <summary>
    /// Evaluate based on both probability and severity scores.
    /// </summary>
    [JsonStringEnumMemberName("SEVERITY")]
    Severity,

    /// <summary>
    /// Evaluate based on probability score only.
    /// </summary>
    [JsonStringEnumMemberName("PROBABILITY")]
    Probability,

    /// <summary>
    /// Fallback value used when the server returns an evaluation method not yet defined in this version of the library.
    /// </summary>
    Unknown
}
