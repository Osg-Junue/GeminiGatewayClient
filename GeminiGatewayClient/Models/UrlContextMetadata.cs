using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Metadata returned when the <see cref="Tool.UrlContext"/> tool retrieves web URLs referenced in the prompt.
/// See <see href="https://ai.google.dev/api/generate-content#UrlContextMetadata"/>.
/// </summary>
public sealed class UrlContextMetadata
{
    /// <summary>
    /// List of URLs retrieved for context along with their retrieval status.
    /// </summary>
    [JsonPropertyName("urlMetadata")]
    public IList<UrlMetadata> UrlMetadata
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
/// Retrieval status for a single URL processed by the <see cref="Tool.UrlContext"/> tool.
/// See <see href="https://ai.google.dev/api/generate-content#UrlMetadata"/>.
/// </summary>
public sealed class UrlMetadata
{
    /// <summary>
    /// The URL retrieved by the tool.
    /// </summary>
    [JsonPropertyName("retrievedUrl")]
    public string? RetrievedUrl { get; set; }

    /// <summary>
    /// Outcome of the URL retrieval attempt.
    /// </summary>
    [JsonPropertyName("urlRetrievalStatus")]
    public UrlRetrievalStatus? UrlRetrievalStatus { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Status of a URL retrieval attempt by the <see cref="Tool.UrlContext"/> tool.
/// See <see href="https://ai.google.dev/api/generate-content#UrlRetrievalStatus"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<UrlRetrievalStatus>))]
public enum UrlRetrievalStatus
{
    /// <summary>
    /// Default value when the retrieval status is unspecified (<c>URL_RETRIEVAL_STATUS_UNSPECIFIED</c>).
    /// </summary>
    [JsonStringEnumMemberName("URL_RETRIEVAL_STATUS_UNSPECIFIED")]
    Unspecified = 0,

    /// <summary>
    /// The URL was retrieved successfully (<c>URL_RETRIEVAL_STATUS_SUCCESS</c>).
    /// </summary>
    [JsonStringEnumMemberName("URL_RETRIEVAL_STATUS_SUCCESS")]
    Success = 1,

    /// <summary>
    /// The URL retrieval failed due to an error (<c>URL_RETRIEVAL_STATUS_ERROR</c>).
    /// </summary>
    [JsonStringEnumMemberName("URL_RETRIEVAL_STATUS_ERROR")]
    Error = 2,

    /// <summary>
    /// The URL retrieval failed because the content is behind a paywall (<c>URL_RETRIEVAL_STATUS_PAYWALL</c>).
    /// </summary>
    [JsonStringEnumMemberName("URL_RETRIEVAL_STATUS_PAYWALL")]
    Paywall = 3,

    /// <summary>
    /// The URL retrieval failed because the content was flagged as unsafe (<c>URL_RETRIEVAL_STATUS_UNSAFE</c>).
    /// </summary>
    [JsonStringEnumMemberName("URL_RETRIEVAL_STATUS_UNSAFE")]
    Unsafe = 4,

    /// <summary>
    /// Fallback value used when the server returns a status not yet defined in this version of the library.
    /// </summary>
    Unknown = -1
}

