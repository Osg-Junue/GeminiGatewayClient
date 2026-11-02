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
    /// Required. IANA MIME type of the media data (see <see cref="MimeTypes"/> for common constants).
    /// </summary>
    [JsonPropertyName("mimeType")]
    public required string MimeType
    {
        get;
        set => field = value ?? "";
    } = "";

    /// <summary>
    /// Required. Raw bytes of the media payload (Base64-encoded in JSON).
    /// </summary>
    [JsonPropertyName("data")]
    public required byte[] Data
    {
        get;
        set => field = value ?? [];
    } = [];

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    internal Blob Clone()
    {
        var clone = (Blob)MemberwiseClone();
        if (ExtensionData is not null)
        {
            clone.ExtensionData = new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal);
        }

        return clone;
    }
}
