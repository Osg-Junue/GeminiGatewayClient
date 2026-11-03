using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Collection of source attributions for a generated candidate response.
/// See <see href="https://ai.google.dev/api/generate-content#citationmetadata"/>.
/// </summary>
public sealed class CitationMetadata
{
    private IList<Citation>? _citations;

    /// <summary>
    /// Citations to sources for a specific response.
    /// </summary>
    [JsonPropertyName("citations")]
    public IList<Citation> Citations
    {
        get => _citations ??= [];
        set => _citations = value!;
    }

    internal IList<Citation>? RawCitations => _citations;

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
