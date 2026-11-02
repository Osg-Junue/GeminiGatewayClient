using System.Text.Json.Serialization;
using GeminiAPIWrapper.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Probability level that a piece of content is harmful in a given <see cref="HarmCategory"/>.
/// See <see href="https://ai.google.dev/api/generate-content#harmprobability"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<HarmProbability>))]
public enum HarmProbability
{
    /// <summary>
    /// Probability is unspecified.
    /// </summary>
    [JsonStringEnumMemberName("HARM_PROBABILITY_UNSPECIFIED")]
    Unspecified,

    /// <summary>
    /// Content has a negligible probability of being unsafe.
    /// </summary>
    [JsonStringEnumMemberName("NEGLIGIBLE")]
    Negligible,

    /// <summary>
    /// Content has a low probability of being unsafe.
    /// </summary>
    [JsonStringEnumMemberName("LOW")]
    Low,

    /// <summary>
    /// Content has a medium probability of being unsafe.
    /// </summary>
    [JsonStringEnumMemberName("MEDIUM")]
    Medium,

    /// <summary>
    /// Content has a high probability of being unsafe.
    /// </summary>
    [JsonStringEnumMemberName("HIGH")]
    High,

    /// <summary>
    /// Fallback value used when the server returns a harm probability not yet defined in this version of the library.
    /// </summary>
    Unknown
}
