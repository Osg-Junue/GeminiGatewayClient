using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// A token string paired with its log probability.
/// </summary>
public sealed class TokenLogProbability
{
    /// <summary>
    /// The token text.
    /// </summary>
    [JsonPropertyName("token")]
    public string Token
    {
        get;
        set => field = value ?? "";
    } = "";

    /// <summary>
    /// Optional. The vocabulary token ID corresponding to <see cref="Token"/>.
    /// </summary>
    [JsonPropertyName("tokenId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TokenId { get; set; }

    /// <summary>
    /// Natural log probability of <see cref="Token"/> at its decoding step.
    /// </summary>
    [JsonPropertyName("logProbability")]
    public float LogProbability { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
