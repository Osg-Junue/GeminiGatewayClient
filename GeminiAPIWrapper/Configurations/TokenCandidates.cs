using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Top candidate tokens and their log probabilities at a single decoding step.
/// </summary>
public sealed class TokenCandidates
{
    /// <summary>
    /// Candidate tokens considered by the model at this decoding position, ordered by descending probability.
    /// </summary>
    [JsonPropertyName("candidates")]
    public List<TokenLogProbability> Candidates { get; set; } = [];
}
