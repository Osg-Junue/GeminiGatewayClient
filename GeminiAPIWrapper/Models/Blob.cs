using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// Inline media bytes. Serialized as a Base64 string on the wire.
/// See <see href="https://ai.google.dev/api/caching#Blob"/>.
/// </summary>
public sealed class Blob
{
    /// <summary>
    /// IANA MIME type of the media data (see <see cref="MimeTypes"/> for common constants).
    /// </summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "";

    /// <summary>
    /// Raw bytes of the media payload (Base64-encoded in JSON).
    /// </summary>
    [JsonPropertyName("data")]
    public byte[] Data { get; set; } = [];
}
