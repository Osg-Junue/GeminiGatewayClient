using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// A token string paired with its log probability.
/// </summary>
public sealed class TokenLogProbability
{
    /// <summary>
    /// The token text.
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>
    /// Natural log probability of <see cref="Token"/> at its decoding step.
    /// </summary>
    [JsonPropertyName("logProbability")]
    public float LogProbability { get; set; }
}
