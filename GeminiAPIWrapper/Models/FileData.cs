using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// URI-based file reference (such as a file uploaded via the Files API or a Cloud Storage URI).
/// See <see href="https://ai.google.dev/api/caching#FileData"/>.
/// </summary>
public sealed class FileData
{
    /// <summary>
    /// Optional. IANA MIME type of the referenced file (see <see cref="MimeTypes"/> for common constants).
    /// </summary>
    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }

    /// <summary>
    /// Required. URI of the file (for example a File API URI or <c>gs://bucket/object</c>).
    /// </summary>
    [JsonPropertyName("fileUri")]
    public required string FileUri { get; set; }
}
