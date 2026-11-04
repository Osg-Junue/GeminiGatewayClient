using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Safety rating evaluated for a single <see cref="HarmCategory"/> on a response candidate.
/// See <see href="https://ai.google.dev/api/generate-content#safetyrating"/>.
/// </summary>
public sealed class SafetyRating
{
    /// <summary>
    /// Harm category evaluated by this rating.
    /// </summary>
    [JsonPropertyName("category")]
    public HarmCategory Category { get; set; }

    /// <summary>
    /// Assessed probability of harm in <see cref="Category"/>.
    /// </summary>
    [JsonPropertyName("probability")]
    public HarmProbability Probability { get; set; }

    /// <summary>
    /// Optional. Numeric probability score (<c>0.0</c> to <c>1.0</c>) for <see cref="Category"/>.
    /// </summary>
    [JsonPropertyName("probabilityScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? ProbabilityScore { get; set; }

    /// <summary>
    /// Optional. Assessed severity of harm in <see cref="Category"/>.
    /// </summary>
    [JsonPropertyName("severity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HarmSeverity? Severity { get; set; }

    /// <summary>
    /// Optional. Numeric severity score (<c>0.0</c> to <c>1.0</c>) for <see cref="Category"/>.
    /// </summary>
    [JsonPropertyName("severityScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? SeverityScore { get; set; }

    /// <summary>
    /// Indicates whether the candidate was blocked due to this safety rating.
    /// </summary>
    [JsonPropertyName("blocked")]
    public bool? Blocked { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Assessed severity level of potential harm in a <see cref="SafetyRating"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<HarmSeverity>))]
public enum HarmSeverity
{
    /// <summary>
    /// Harm severity is unspecified.
    /// </summary>
    [JsonStringEnumMemberName("HARM_SEVERITY_UNSPECIFIED")]
    Unspecified = 0,

    /// <summary>
    /// Negligible severity of harm.
    /// </summary>
    [JsonStringEnumMemberName("HARM_SEVERITY_NEGLIGIBLE")]
    Negligible = 1,

    /// <summary>
    /// Low severity of harm.
    /// </summary>
    [JsonStringEnumMemberName("HARM_SEVERITY_LOW")]
    Low = 2,

    /// <summary>
    /// Medium severity of harm.
    /// </summary>
    [JsonStringEnumMemberName("HARM_SEVERITY_MEDIUM")]
    Medium = 3,

    /// <summary>
    /// High severity of harm.
    /// </summary>
    [JsonStringEnumMemberName("HARM_SEVERITY_HIGH")]
    High = 4,

    /// <summary>
    /// Fallback value used when the server returns a severity level not yet defined in this version of the library.
    /// </summary>
    Unknown = -1
}
