using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// Content-filtering metadata and safety ratings for the input prompt in a <see cref="GenerateContentResponse"/>.
/// See <see href="https://ai.google.dev/api/generate-content#PromptFeedback"/>.
/// </summary>
public sealed class PromptFeedback
{
    /// <summary>
    /// Optional. If set, the prompt was blocked and no candidates are returned.
    /// </summary>
    [JsonPropertyName("blockReason")]
    public BlockReason? BlockReason { get; set; }

    /// <summary>
    /// Safety ratings evaluated for the prompt across categories.
    /// </summary>
    [JsonPropertyName("safetyRatings")]
    public IList<SafetyRating> SafetyRatings { get; set; } = [];
}
