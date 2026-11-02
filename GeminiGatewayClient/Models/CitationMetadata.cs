using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Collection of source attributions for a generated candidate response.
/// See <see href="https://ai.google.dev/api/generate-content#citationmetadata"/>.
/// </summary>
public sealed class CitationMetadata
{
    /// <summary>
    /// Citations to sources for a specific response.
    /// </summary>
    [JsonPropertyName("citations")]
    public IList<Citation> Citations
    {
        get => field;
        set => field = value ?? [];
    } = [];

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
