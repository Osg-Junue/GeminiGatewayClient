using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// A single response candidate returned by the model.
/// See <see href="https://ai.google.dev/api/generate-content#candidate"/>.
/// </summary>
public sealed class Candidate
{
    /// <summary>
    /// Zero-based index of this candidate in the list of response candidates.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Generated content returned by the model for this candidate.
    /// </summary>
    [JsonPropertyName("content")]
    public Content? Content { get; set; }

    /// <summary>
    /// Reason why the model stopped generating tokens for this candidate.
    /// </summary>
    [JsonPropertyName("finishReason")]
    public FinishReason? FinishReason { get; set; }

    /// <summary>
    /// Optional. Human-readable message describing why the model stopped generating tokens.
    /// </summary>
    [JsonPropertyName("finishMessage")]
    public string? FinishMessage { get; set; }

    /// <summary>
    /// Safety ratings evaluated for this candidate response.
    /// </summary>
    [JsonPropertyName("safetyRatings")]
    public IList<SafetyRating> SafetyRatings { get; set; } = [];

    /// <summary>
    /// Number of tokens in this candidate response.
    /// </summary>
    [JsonPropertyName("tokenCount")]
    public int? TokenCount { get; set; }

    /// <summary>
    /// Version of the model used to generate this candidate.
    /// </summary>
    [JsonPropertyName("modelVersion")]
    public string? ModelVersion { get; set; }

    /// <summary>
    /// Attribution and citation metadata for sources referenced in this candidate.
    /// </summary>
    [JsonPropertyName("citationMetadata")]
    public CitationMetadata? CitationMetadata { get; set; }

    /// <summary>
    /// Average log probability across tokens in this candidate.
    /// </summary>
    [JsonPropertyName("avgLogprobs")]
    public double? AvgLogprobs { get; set; }

    /// <summary>
    /// Detailed per-token log probability information when requested via <see cref="GenerationConfig.ResponseLogprobs"/>.
    /// </summary>
    [JsonPropertyName("logprobsResult")]
    public LogprobsResult? LogprobsResult { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type (for example <c>groundingMetadata</c> or <c>urlContextMetadata</c>).
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
