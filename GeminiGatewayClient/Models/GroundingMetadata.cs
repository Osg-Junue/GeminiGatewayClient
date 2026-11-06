using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Metadata returned when a candidate response is grounded via Google Search or retrieval sources.
/// See <see href="https://ai.google.dev/api/generate-content#GroundingMetadata"/>.
/// </summary>
public sealed class GroundingMetadata
{
    /// <summary>
    /// Web search queries executed by the model to gather grounding context.
    /// </summary>
    [JsonPropertyName("webSearchQueries")]
    public IList<string> WebSearchQueries { get => field ??= []; set; }

    /// <summary>
    /// Optional Google Search entry point widget metadata (HTML/CSS and SDK blob) for rendering search suggestions.
    /// </summary>
    [JsonPropertyName("searchEntryPoint")]
    public SearchEntryPoint? SearchEntryPoint { get; set; }

    /// <summary>
    /// Supporting grounding chunks (web pages or retrieved context documents) referenced by the candidate.
    /// </summary>
    [JsonPropertyName("groundingChunks")]
    public IList<GroundingChunk> GroundingChunks { get => field ??= []; set; }

    /// <summary>
    /// Mappings from specific spans of the candidate response text to indices in <see cref="GroundingChunks"/>.
    /// </summary>
    [JsonPropertyName("groundingSupports")]
    public IList<GroundingSupport> GroundingSupports { get => field ??= []; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Google Search entry point metadata returned as part of <see cref="GroundingMetadata"/>.
/// See <see href="https://ai.google.dev/api/generate-content#SearchEntryPoint"/>.
/// </summary>
public sealed class SearchEntryPoint
{
    /// <summary>
    /// Web content snippet (HTML and CSS) that can be embedded in a web page or application webview.
    /// </summary>
    [JsonPropertyName("renderedContent")]
    public string? RenderedContent { get; set; }

    /// <summary>
    /// Base64-encoded JSON array of <c>(search term, search URL)</c> tuples.
    /// </summary>
    [JsonPropertyName("sdkBlob")]
    public string? SdkBlob { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// A single grounding source chunk referenced by <see cref="GroundingMetadata.GroundingChunks"/>.
/// See <see href="https://ai.google.dev/api/generate-content#GroundingChunk"/>.
/// </summary>
public sealed class GroundingChunk
{
    /// <summary>
    /// Grounding chunk originating from a web page found via Google Search.
    /// </summary>
    [JsonPropertyName("web")]
    public GroundingChunkWeb? Web { get; set; }

    /// <summary>
    /// Grounding chunk originating from a retrieval/document corpus.
    /// </summary>
    [JsonPropertyName("retrievedContext")]
    public GroundingChunkRetrievedContext? RetrievedContext { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Web source details for a <see cref="GroundingChunk"/>.
/// See <see href="https://ai.google.dev/api/generate-content#Web"/>.
/// </summary>
public sealed class GroundingChunkWeb
{
    /// <summary>
    /// URI reference of the web chunk.
    /// </summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    /// <summary>
    /// Title of the web page or source.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Domain of the web source, when provided by the endpoint.
    /// </summary>
    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Retrieved document/corpus context details for a <see cref="GroundingChunk"/>.
/// </summary>
public sealed class GroundingChunkRetrievedContext
{
    /// <summary>
    /// URI reference of the retrieved document.
    /// </summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    /// <summary>
    /// Title of the retrieved document.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Text excerpt of the retrieved chunk.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Links a <see cref="GroundingSegment"/> of the generated response to one or more <see cref="GroundingChunk"/> entries.
/// See <see href="https://ai.google.dev/api/generate-content#GroundingSupport"/>.
/// </summary>
public sealed class GroundingSupport
{
    /// <summary>
    /// The text segment in the candidate response backed by the referenced chunks.
    /// </summary>
    [JsonPropertyName("segment")]
    public GroundingSegment? Segment { get; set; }

    /// <summary>
    /// Zero-based indices into <see cref="GroundingMetadata.GroundingChunks"/> supporting <see cref="Segment"/>.
    /// </summary>
    [JsonPropertyName("groundingChunkIndices")]
    public IList<int> GroundingChunkIndices { get => field ??= []; set; }

    /// <summary>
    /// Confidence scores (<c>[0.0, 1.0]</c>) corresponding to <see cref="GroundingChunkIndices"/>.
    /// </summary>
    [JsonPropertyName("confidenceScores")]
    public IList<double> ConfidenceScores
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

/// <summary>
/// Identifies a span of a <see cref="Part"/> in the candidate <see cref="Content"/>.
/// Offsets (<see cref="StartIndex"/> and <see cref="EndIndex"/>) are measured in UTF-8 bytes in the Gemini wire protocol.
/// See <see href="https://ai.google.dev/api/generate-content#Segment"/>.
/// </summary>
public sealed class GroundingSegment
{
    /// <summary>
    /// Zero-based index of the <see cref="Part"/> within <see cref="Content.Parts"/>. The API usually omits it.
    /// </summary>
    [JsonPropertyName("partIndex")]
    public int? PartIndex { get; set; }

    /// <summary>
    /// Inclusive start offset of the segment in the response text, measured in UTF-8 bytes.
    /// </summary>
    [JsonPropertyName("startIndex")]
    public int? StartIndex { get; set; }

    /// <summary>
    /// Exclusive end offset of the segment in the response text, measured in UTF-8 bytes.
    /// </summary>
    [JsonPropertyName("endIndex")]
    public int? EndIndex { get; set; }

    /// <summary>
    /// The text corresponding to the segment from the response.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
