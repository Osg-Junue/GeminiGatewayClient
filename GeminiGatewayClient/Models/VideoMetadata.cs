using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

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
    public VideoOffset? StartOffset { get; set; }

    /// <summary>
    /// Optional. End offset of the video clip to process.
    /// </summary>
    [JsonPropertyName("endOffset")]
    public VideoOffset? EndOffset { get; set; }

    /// <summary>
    /// Optional. Frame rate (frames per second) at which the video should be sampled.
    /// </summary>
    [JsonPropertyName("fps")]
    public double? Fps { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Time offset within a video stream. Serialized on the wire as a Protobuf <c>Duration</c> string (for example <c>"40s"</c> or <c>"1.5s"</c>)
/// per the Gemini REST API specification.
/// </summary>
[JsonConverter(typeof(VideoOffsetJsonConverter))]
public sealed class VideoOffset
{
    /// <summary>
    /// Whole seconds of the time offset.
    /// </summary>
    public int? Seconds { get; set; }

    /// <summary>
    /// Sub-second nanosecond fraction of the time offset.
    /// </summary>
    public int? Nanos { get; set; }

    /// <summary>
    /// Creates a <see cref="VideoOffset"/> from a <see cref="TimeSpan"/>.
    /// </summary>
    /// <param name="offset">The time offset.</param>
    /// <returns>A new <see cref="VideoOffset"/>.</returns>
    public static VideoOffset FromTimeSpan(TimeSpan offset)
    {
        var totalTicks = offset.Ticks;
        var seconds = checked((int)(totalTicks / TimeSpan.TicksPerSecond));
        var remainderTicks = totalTicks % TimeSpan.TicksPerSecond;
        var nanos = (int)(remainderTicks * 100);
        return new VideoOffset
        {
            Seconds = seconds,
            Nanos = nanos == 0 ? null : nanos
        };
    }

    /// <summary>
    /// Converts this <see cref="VideoOffset"/> to a <see cref="TimeSpan"/>.
    /// </summary>
    /// <returns>The equivalent <see cref="TimeSpan"/>.</returns>
    public TimeSpan ToTimeSpan()
    {
        var ticks = (Seconds ?? 0L) * TimeSpan.TicksPerSecond + (Nanos ?? 0L) / 100L;
        return TimeSpan.FromTicks(ticks);
    }
}

/// <summary>
/// Converts <see cref="VideoOffset"/> to and from the Protobuf <c>Duration</c> wire format, such as <c>"40s"</c> or <c>"10.5s"</c>.
/// </summary>
public sealed class VideoOffsetJsonConverter : JsonConverter<VideoOffset>
{
    private const decimal NanosPerSecond = 1_000_000_000m;

    /// <inheritdoc />
    public override VideoOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (text is null || !text.EndsWith('s') ||
            !decimal.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Number, CultureInfo.InvariantCulture, out var total) ||
            Math.Abs(total) > int.MaxValue)
        {
            throw new JsonException($"Invalid VideoOffset duration '{text}'.");
        }

        var seconds = (int)decimal.Truncate(total);
        var nanos = (int)((total - seconds) * NanosPerSecond);
        return new VideoOffset { Seconds = seconds, Nanos = nanos == 0 ? null : nanos };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, VideoOffset value, JsonSerializerOptions options)
    {
        var total = (value.Seconds ?? 0) + ((value.Nanos ?? 0) / NanosPerSecond);
        writer.WriteStringValue(total.ToString("0.#########", CultureInfo.InvariantCulture) + "s");
    }
}
