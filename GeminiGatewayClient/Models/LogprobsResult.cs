using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

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
    public IList<TokenCandidates> TopCandidates
    {
        get => field ??= [];
        set => field = value!;
    } = null!;

    /// <summary>
    /// Tokens actually chosen by the model and their log probabilities at each decoding step.
    /// </summary>
    [JsonPropertyName("chosenCandidates")]
    public IList<TokenLogProbability> ChosenCandidates
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
