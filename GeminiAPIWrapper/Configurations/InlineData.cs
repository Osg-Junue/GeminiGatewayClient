using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Inline media bytes encoded as Base64.
/// See <see href="https://ai.google.dev/api/caching#Blob"/>.
/// </summary>
public sealed class InlineData
{
    /// <summary>
    /// IANA MIME type of the media data (see <see cref="MimeTypes"/> for common constants).
    /// </summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "";

    /// <summary>
    /// Base64-encoded raw bytes of the media payload.
    /// </summary>
    [JsonPropertyName("data")]
    public string Data { get; set; } = "";
}
