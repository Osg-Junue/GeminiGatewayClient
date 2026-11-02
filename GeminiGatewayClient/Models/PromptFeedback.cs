using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

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
    public IList<SafetyRating> SafetyRatings
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
