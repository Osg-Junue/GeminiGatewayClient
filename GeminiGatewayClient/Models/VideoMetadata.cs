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

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    internal VideoMetadata Clone()
    {
        var clone = (VideoMetadata)MemberwiseClone();
        clone.StartOffset = StartOffset?.Clone();
        clone.EndOffset = EndOffset?.Clone();
        if (ExtensionData is not null)
        {
            clone.ExtensionData = new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal);
        }

        return clone;
    }
}

/// <summary>
/// Time offset within a video stream. Serialized on the wire as a Protobuf <c>Duration</c> string (for example <c>"40s"</c> or <c>"1.5s"</c>)
/// per the Gemini REST API specification, while accepting both Protobuf duration strings and <c>{"seconds": ..., "nanos": ...}</c> objects when deserializing.
/// </summary>
[JsonConverter(typeof(VideoOffsetJsonConverter))]
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

    internal VideoOffset Clone() => (VideoOffset)MemberwiseClone();

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
/// Converts <see cref="VideoOffset"/> to and from the Gemini REST API Protobuf <c>Duration</c> wire format
/// (for example <c>"40s"</c> or <c>"10.5s"</c>), while also accepting <c>{"seconds": ..., "nanos": ...}</c> JSON objects.
/// </summary>
public sealed class VideoOffsetJsonConverter : JsonConverter<VideoOffset>
{
    /// <inheritdoc />
    public override VideoOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new VideoOffset();
            }

            if (TryParseDurationString(raw.AsSpan(), out var parsedOffset))
            {
                return parsedOffset;
            }

            throw new JsonException($"Invalid VideoOffset duration string '{raw}'.");
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            int? seconds = null;
            int? nanos = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return new VideoOffset { Seconds = seconds, Nanos = nanos };
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("seconds"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var s))
                    {
                        seconds = s;
                    }
                    else if (reader.TokenType == JsonTokenType.String &&
                             int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedS))
                    {
                        seconds = parsedS;
                    }
                }
                else if (reader.ValueTextEquals("nanos"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
                    {
                        nanos = n;
                    }
                    else if (reader.TokenType == JsonTokenType.String &&
                             int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedN))
                    {
                        nanos = parsedN;
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            throw new JsonException("Unexpected end of JSON while reading VideoOffset object.");
        }

        throw new JsonException($"Unexpected JSON token {reader.TokenType} when reading VideoOffset.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, VideoOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var seconds = value.Seconds ?? 0;
        var nanos = value.Nanos ?? 0;
        if (nanos == 0)
        {
            Span<char> buffer = stackalloc char[16];
            if (seconds.TryFormat(buffer, out var charsWritten, provider: CultureInfo.InvariantCulture))
            {
                buffer[charsWritten] = 's';
                writer.WriteStringValue(buffer[..(charsWritten + 1)]);
                return;
            }
        }

        var totalSeconds = seconds + (nanos / 1_000_000_000.0);
        if (seconds >= 0 && nanos > 0 && nanos < 1_000_000_000)
        {
            Span<char> fracBuffer = stackalloc char[9];
            nanos.TryFormat(fracBuffer, out _, "D9", CultureInfo.InvariantCulture);
            var fracSpan = fracBuffer.TrimEnd('0');
            writer.WriteStringValue(string.Create(CultureInfo.InvariantCulture, $"{seconds}.{fracSpan}s"));
            return;
        }

        writer.WriteStringValue(string.Create(CultureInfo.InvariantCulture, $"{totalSeconds:0.#########}s"));
    }

    private static bool TryParseDurationString(ReadOnlySpan<char> span, out VideoOffset offset)
    {
        offset = null!;
        span = span.Trim();
        if (span.IsEmpty || span[^1] != 's')
        {
            return false;
        }

        var numberSpan = span[..^1].TrimEnd();
        if (numberSpan.IsEmpty)
        {
            return false;
        }

        var dotIndex = numberSpan.IndexOf('.');
        if (dotIndex < 0)
        {
            if (!int.TryParse(numberSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wholeSeconds))
            {
                return false;
            }

            offset = new VideoOffset { Seconds = wholeSeconds };
            return true;
        }

        var secSpan = numberSpan[..dotIndex];
        var fracSpan = numberSpan[(dotIndex + 1)..];
        var isNegative = secSpan.StartsWith('-');

        var whole = 0;
        if (!secSpan.IsEmpty && secSpan is not ['-'] &&
            !int.TryParse(secSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out whole))
        {
            return false;
        }

        var nanos = 0;
        if (!fracSpan.IsEmpty)
        {
            if (fracSpan.Length > 9)
            {
                fracSpan = fracSpan[..9];
            }

            Span<char> padded = stackalloc char[9];
            padded.Fill('0');
            fracSpan.CopyTo(padded);
            if (!int.TryParse(padded, NumberStyles.None, CultureInfo.InvariantCulture, out nanos))
            {
                return false;
            }

            if (isNegative)
            {
                nanos = -nanos;
            }
        }

        offset = new VideoOffset
        {
            Seconds = whole,
            Nanos = nanos == 0 ? null : nanos
        };
        return true;
    }
}
