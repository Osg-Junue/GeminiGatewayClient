using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

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
    public IList<TokenCandidates> TopCandidates { get; set; } = [];

    /// <summary>
    /// Tokens actually chosen by the model and their log probabilities at each decoding step.
    /// </summary>
    [JsonPropertyName("chosenCandidates")]
    public IList<TokenLogProbability> ChosenCandidates { get; set; } = [];
}
