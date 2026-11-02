using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Top candidate tokens and their log probabilities at a single decoding step.
/// </summary>
public sealed class TokenCandidates
{
    /// <summary>
    /// Candidate tokens considered by the model at this decoding position, ordered by descending probability.
    /// </summary>
    [JsonPropertyName("candidates")]
    public IList<TokenLogProbability> Candidates
    {
        get => field ??= [];
        set => field = value!;
    } = null!;

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
