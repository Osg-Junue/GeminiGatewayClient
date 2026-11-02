using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

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
    public List<Citation> Citations { get; set; } = [];
}
