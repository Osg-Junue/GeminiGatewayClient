using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using GeminiAPIWrapper.Models;
using GeminiAPIWrapper.Serialization;

namespace GeminiAPIWrapper.Http;

/// <summary>
/// Reads <c>:streamGenerateContent</c> response bodies in either Server-Sent Events or chunked JSON array format,
/// surfacing in-stream error payloads as <see cref="GeminiApiException"/>.
/// </summary>
internal static class GeminiStreamReader
{
    /// <summary>
    /// Reads chunks from <paramref name="stream"/> in the specified <paramref name="format"/>.
    /// </summary>
    public static IAsyncEnumerable<GenerateContentResponse> ReadAsync(
        Stream stream,
        GeminiStreamingFormat format,
        HttpStatusCode statusCode,
        StreamIdleWatchdog watchdog) =>
        format == GeminiStreamingFormat.ServerSentEvents
            ? ReadServerSentEventsAsync(stream, statusCode, watchdog)
            : ReadJsonArrayAsync(stream, statusCode, watchdog);

    private static async IAsyncEnumerable<GenerateContentResponse> ReadServerSentEventsAsync(
        Stream stream,
        HttpStatusCode statusCode,
        StreamIdleWatchdog watchdog)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var dataBuffer = new ArrayBufferWriter<byte>(1024);
        ArrayBufferWriter<byte>? nonSseBuffer = null;
        var sawSseEvent = false;

        while (true)
        {
            watchdog.BeforeRead();
            var line = await reader.ReadLineAsync(watchdog.Token).ConfigureAwait(false);
            watchdog.AfterRead();

            // End of stream or blank line: dispatch the buffered event (if any).
            if (line is null || line.Length == 0)
            {
                if (dataBuffer.WrittenCount > 0)
                {
                    sawSseEvent = true;
                    var response = DeserializeUtf8Chunk(dataBuffer.WrittenSpan, statusCode);
                    dataBuffer.Clear();
                    yield return response;
                }

                if (line is null)
                {
                    if (!sawSseEvent && nonSseBuffer is { WrittenCount: > 0 })
                    {
                        yield return DeserializeUtf8Chunk(nonSseBuffer.WrittenSpan, statusCode);
                    }

                    yield break;
                }

                continue;
            }

            if (line[0] == ':')
            {
                // Comment / keep-alive line.
                sawSseEvent = true;
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (line.StartsWith("event:", StringComparison.Ordinal) ||
                    line.StartsWith("id:", StringComparison.Ordinal) ||
                    line.StartsWith("retry:", StringComparison.Ordinal))
                {
                    sawSseEvent = true;
                    continue;
                }

                // A proxy or upstream server may return a raw JSON error payload with HTTP 200 instead of SSE framing.
                if (!sawSseEvent)
                {
                    Encoding.UTF8.GetBytes(line.AsSpan(), nonSseBuffer ??= new ArrayBufferWriter<byte>(256));
                }

                continue;
            }

            var payload = line.AsSpan(5);
            if (payload.Length > 0 && payload[0] == ' ')
            {
                payload = payload[1..];
            }

            if (payload.SequenceEqual("[DONE]"))
            {
                sawSseEvent = true;
                continue;
            }

            if (dataBuffer.WrittenCount > 0)
            {
                dataBuffer.GetSpan(1)[0] = (byte)'\n';
                dataBuffer.Advance(1);
            }

            Encoding.UTF8.GetBytes(payload, dataBuffer);
        }
    }

    private static GenerateContentResponse DeserializeUtf8Chunk(ReadOnlySpan<byte> utf8Json, HttpStatusCode statusCode)
    {
        try
        {
            if (LooksLikeErrorPayload(utf8Json))
            {
                var docReader = new Utf8JsonReader(utf8Json);
                using var document = JsonDocument.ParseValue(ref docReader);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    root = root[0];
                }

                if (GeminiErrorParser.TryCreateFromStreamPayload(root, statusCode) is { } streamError)
                {
                    throw streamError;
                }
            }

            return JsonSerializer.Deserialize(utf8Json, GeminiJsonContext.Default.GenerateContentResponse)
                   ?? throw new GeminiApiException("Received an empty (null) Gemini streaming chunk.", statusCode);
        }
        catch (JsonException ex)
        {
            throw new GeminiApiException("Failed to deserialize a Gemini streaming chunk.", statusCode, null, ex);
        }
    }

    private static bool LooksLikeErrorPayload(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                return reader.ValueTextEquals("error"u8);
            }
        }

        return false;
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ReadJsonArrayAsync(
        Stream stream,
        HttpStatusCode statusCode,
        StreamIdleWatchdog watchdog)
    {
        await using var enumerator = JsonSerializer
            .DeserializeAsyncEnumerable(stream, GeminiJsonContext.Default.JsonElement, watchdog.Token)
            .GetAsyncEnumerator(watchdog.Token);

        while (true)
        {
            GenerateContentResponse current;
            try
            {
                watchdog.BeforeRead();
                var hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                watchdog.AfterRead();

                if (!hasNext)
                {
                    yield break;
                }

                current = ToResponse(enumerator.Current, statusCode);
            }
            catch (JsonException ex)
            {
                throw new GeminiApiException("Failed to deserialize a Gemini streaming chunk.", statusCode, null, ex);
            }

            yield return current;
        }
    }

    private static GenerateContentResponse ToResponse(JsonElement element, HttpStatusCode statusCode)
    {
        if (GeminiErrorParser.TryCreateFromStreamPayload(element, statusCode) is { } streamError)
        {
            throw streamError;
        }

        return element.Deserialize(GeminiJsonContext.Default.GenerateContentResponse)
               ?? throw new GeminiApiException("Received an empty (null) Gemini streaming chunk.", statusCode);
    }
}
