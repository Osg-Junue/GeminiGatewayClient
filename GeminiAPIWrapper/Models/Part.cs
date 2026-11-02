using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// A single segment of a multi-part <see cref="Content"/> message (text, inline media, file reference, function call, or function response).
/// See <see href="https://ai.google.dev/api/caching#Part"/>.
/// </summary>
public sealed class Part
{
    /// <summary>
    /// Optional. Inline text content.
    /// </summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>
    /// Optional. Indicates whether this part contains model reasoning/thought summary rather than final output text.
    /// </summary>
    [JsonPropertyName("thought")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Thought { get; set; }

    /// <summary>
    /// Optional. Opaque signature for thought parts returned by thinking models during multi-turn tool use.
    /// Must be sent back unchanged in subsequent turns.
    /// </summary>
    [JsonPropertyName("thoughtSignature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThoughtSignature { get; set; }

    /// <summary>
    /// Optional. Inline media bytes.
    /// </summary>
    [JsonPropertyName("inlineData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Blob? InlineData { get; set; }

    /// <summary>
    /// Optional. URI-based file reference.
    /// </summary>
    [JsonPropertyName("fileData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileData? FileData { get; set; }

    /// <summary>
    /// Optional. Predicted function call requested by the model.
    /// </summary>
    [JsonPropertyName("functionCall")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionCall? FunctionCall { get; set; }

    /// <summary>
    /// Optional. Output of a function call returned to the model.
    /// </summary>
    [JsonPropertyName("functionResponse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionResponse? FunctionResponse { get; set; }

    /// <summary>
    /// Optional. Video clipping and frame-rate metadata associated with media in this part.
    /// </summary>
    [JsonPropertyName("videoMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoMetadata? VideoMetadata { get; set; }

    /// <summary>
    /// Creates a text part.
    /// </summary>
    /// <param name="text">The text content.</param>
    /// <returns>A new <see cref="Part"/>.</returns>
    public static Part FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Part { Text = text };
    }

    /// <summary>
    /// Creates an inline media part. Prefer <see cref="FromUri"/> (Files API) for payloads larger than about 20 MB.
    /// </summary>
    /// <param name="data">The raw media bytes.</param>
    /// <param name="mimeType">The IANA MIME type of <paramref name="data"/> (see <see cref="MimeTypes"/>).</param>
    /// <returns>A new <see cref="Part"/>.</returns>
    public static Part FromBytes(ReadOnlySpan<byte> data, string mimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        return new Part { InlineData = new Blob { MimeType = mimeType, Data = data.ToArray() } };
    }

    /// <summary>
    /// Creates a file reference part (for example a Files API URI or a Cloud Storage URI).
    /// </summary>
    /// <param name="fileUri">The file URI.</param>
    /// <param name="mimeType">Optional IANA MIME type of the file.</param>
    /// <returns>A new <see cref="Part"/>.</returns>
    public static Part FromUri(string fileUri, string? mimeType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileUri);
        return new Part { FileData = new FileData { FileUri = fileUri, MimeType = mimeType } };
    }

    /// <summary>
    /// Creates a function response part that returns the result of a <see cref="Models.FunctionCall"/> to the model.
    /// </summary>
    /// <param name="name">The name of the function that was called.</param>
    /// <param name="response">The function output as a JSON object.</param>
    /// <param name="id">The <see cref="FunctionCall.Id"/> of the call being answered, when the model provided one.</param>
    /// <returns>A new <see cref="Part"/>.</returns>
    public static Part FromFunctionResponse(string name, JsonElement response, string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Part { FunctionResponse = new FunctionResponse { Name = name, Response = response, Id = id } };
    }
}
