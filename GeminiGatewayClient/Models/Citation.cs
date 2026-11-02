using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Source attribution for a span of generated text within a candidate.
/// See <see href="https://ai.google.dev/api/generate-content#citationsource"/>.
/// </summary>
public sealed class Citation
{
    /// <summary>
    /// Start byte/character offset in the response text where the cited span begins.
    /// </summary>
    [JsonPropertyName("startIndex")]
    public int? StartIndex { get; set; }

    /// <summary>
    /// End byte/character offset in the response text where the cited span ends.
    /// </summary>
    [JsonPropertyName("endIndex")]
    public int? EndIndex { get; set; }

    /// <summary>
    /// URI of the attributed source.
    /// </summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    /// <summary>
    /// License associated with the cited source, if any.
    /// </summary>
    [JsonPropertyName("license")]
    public string? License { get; set; }

    /// <summary>
    /// Title of the cited source, if available.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Publication date of the cited source, if available.
    /// </summary>
    [JsonPropertyName("publicationDate")]
    public PublicationDate? PublicationDate { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
