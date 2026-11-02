using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// Metadata describing how an input video should be clipped or sampled before being processed by the model.
/// See <see href="https://ai.google.dev/api/caching#VideoMetadata"/>.
/// </summary>
public sealed class VideoMetadata
{
    /// <summary>
    /// Optional. Start offset of the video clip to process.
    /// </summary>
    [JsonPropertyName("startOffset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoOffset? StartOffset { get; set; }

    /// <summary>
    /// Optional. End offset of the video clip to process.
    /// </summary>
    [JsonPropertyName("endOffset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoOffset? EndOffset { get; set; }

    /// <summary>
    /// Optional. Frame rate (frames per second) at which the video should be sampled.
    /// </summary>
    [JsonPropertyName("fps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Fps { get; set; }
}

/// <summary>
/// Time offset within a video stream.
/// </summary>
public sealed class VideoOffset
{
    /// <summary>
    /// Whole seconds of the time offset.
    /// </summary>
    [JsonPropertyName("seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seconds { get; set; }

    /// <summary>
    /// Sub-second nanosecond fraction of the time offset.
    /// </summary>
    [JsonPropertyName("nanos")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Nanos { get; set; }
}
