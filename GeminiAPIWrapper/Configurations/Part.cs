using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

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
    /// </summary>
    [JsonPropertyName("thoughtSignature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThoughtSignature { get; set; }

    /// <summary>
    /// Optional. Inline media bytes (Base64-encoded).
    /// </summary>
    [JsonPropertyName("inlineData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InlineData? InlineData { get; set; }

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
}
