using System.Text.Json;
using System.Text.Json.Serialization;

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
