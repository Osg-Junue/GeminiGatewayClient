using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Per-token log probability details for a generated candidate.
/// See <see href="https://ai.google.dev/api/generate-content#logprobsresult"/>.
/// </summary>
public sealed class LogprobsResult
{
    /// <summary>
    /// Top candidate tokens and their log probabilities at each decoding step.
    /// </summary>
    [JsonPropertyName("topCandidates")]
    public List<TokenCandidates> TopCandidates { get; set; } = [];

    /// <summary>
    /// Tokens actually chosen by the model and their log probabilities at each decoding step.
    /// </summary>
    [JsonPropertyName("chosenCandidates")]
    public List<TokenLogProbability> ChosenCandidates { get; set; } = [];
}
