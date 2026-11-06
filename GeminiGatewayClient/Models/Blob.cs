using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Inline media bytes. Serialized as a Base64 string on the wire.
/// See <see href="https://ai.google.dev/api/caching#Blob"/>.
/// </summary>
public sealed class Blob
{
    /// <summary>
    /// IANA MIME type of the media data (see <see cref="MimeTypes"/> for common constants).
    /// Must be set on requests; a response may omit it, in which case it is empty.
    /// </summary>
    [JsonPropertyName("mimeType")]
    public string MimeType
    {
        get;
        set => field = value ?? "";
    } = "";

    /// <summary>
    /// Raw bytes of the media payload (Base64-encoded in JSON).
    /// Must be set on requests; a response may omit it, in which case it is empty.
    /// </summary>
    [JsonPropertyName("data")]
    public byte[] Data
    {
        get;
        set => field = value ?? [];
    } = [];

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
