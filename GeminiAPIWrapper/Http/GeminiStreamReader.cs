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
        var data = new StringBuilder();

        while (true)
        {
            watchdog.Reset();
            var line = await reader.ReadLineAsync(watchdog.Token).ConfigureAwait(false);

            // End of stream or blank line: dispatch the buffered event (if any).
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return DeserializeChunk(data.ToString(), statusCode);
                    data.Clear();
                }

                if (line is null)
                {
                    yield break;
                }

                continue;
            }

            if (line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                // Comment / keep-alive line, or a field we do not use (event:, id:, retry:).
                continue;
            }

            var payload = line.AsSpan(5);
            if (payload.Length > 0 && payload[0] == ' ')
            {
                payload = payload[1..];
            }

            if (payload.SequenceEqual("[DONE]"))
            {
                continue;
            }

            if (data.Length > 0)
            {
                data.Append('\n');
            }

            data.Append(payload);
        }
    }

    private static GenerateContentResponse DeserializeChunk(string json, HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ToResponse(document.RootElement, statusCode);
        }
        catch (JsonException ex)
        {
            throw new GeminiApiException("Failed to deserialize a Gemini streaming chunk.", statusCode, null, ex);
        }
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
                watchdog.Reset();
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
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
