using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GeminiGatewayClient.Models;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Http;

/// <summary>
/// Turns successful HTTP response bodies into models, surfacing error payloads sent with a 2xx status as <see cref="GeminiApiException"/>.
/// </summary>
internal static class GeminiResponseReader
{
    /// <summary>
    /// Parses a complete JSON body.
    /// </summary>
    public static TResponse Parse<TResponse>(
        ReadOnlySpan<byte> utf8Json,
        JsonTypeInfo<TResponse> typeInfo,
        Func<TResponse, IDictionary<string, JsonElement>?> extensionDataSelector,
        HttpStatusCode statusCode)
        where TResponse : class
    {
        // Some gateways prepend a byte order mark, which JSON does not allow.
        utf8Json = utf8Json.StartsWith(Encoding.UTF8.Preamble) ? utf8Json[Encoding.UTF8.Preamble.Length..] : utf8Json;

        try
        {
            var response = JsonSerializer.Deserialize(utf8Json, typeInfo)
                           ?? throw new GeminiApiException("The Gemini API returned an empty (null) response.", statusCode);

            if (FindError(extensionDataSelector(response)) is { } error)
            {
                throw GeminiErrorParser.FromErrorElement(error, ToBoundedString(utf8Json));
            }

            return response;
        }
        catch (JsonException ex)
        {
            throw GeminiErrorParser.FromUnusableBody("Failed to deserialize the Gemini API response.", ToBoundedString(utf8Json), statusCode, ex);
        }
    }

    /// <summary>
    /// Reads the chunks of a <c>:streamGenerateContent</c> response. The body itself shows its format, whatever its
    /// <c>Content-Type</c> says: an event stream, the JSON array Gemini sends when <c>alt=sse</c> did not reach it, or a single
    /// JSON error or HTML page from a gateway, which is read whole.
    /// </summary>
    public static async IAsyncEnumerable<GenerateContentResponse> ReadChunksAsync(
        ResponseBodyStream body,
        HttpStatusCode statusCode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var first = await body.PeekAsync(cancellationToken).ConfigureAwait(false);
        if (first is (byte)'{' or (byte)'<')
        {
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            yield return ParseChunk(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), statusCode);
            yield break;
        }

        var chunks = first == (byte)'['
            ? ReadJsonArrayAsync(body, statusCode, cancellationToken)
            : ReadEventsAsync(body, statusCode, cancellationToken);
        var isEmpty = true;
        await foreach (var chunk in chunks.ConfigureAwait(false))
        {
            isEmpty = false;
            yield return chunk;
        }

        if (isEmpty)
        {
            throw GeminiErrorParser.FromUnusableBody("The Gemini API returned an empty streaming response.", SkipToJson(body.Head), statusCode);
        }
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ReadEventsAsync(
        ResponseBodyStream body,
        HttpStatusCode statusCode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var parser = SseParser.Create(body, (type, data) => ParseEvent(type, data, statusCode));
        await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.Data is { } chunk)
            {
                yield return chunk;
            }
        }
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ReadJsonArrayAsync(
        ResponseBodyStream body,
        HttpStatusCode statusCode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chunks = JsonSerializer.DeserializeAsyncEnumerable(body, GeminiJsonContext.Default.GenerateContentResponse, cancellationToken);
        var enumerator = chunks.GetAsyncEnumerator(cancellationToken);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }
                }
                catch (JsonException ex)
                {
                    throw GeminiErrorParser.FromUnusableBody("Failed to deserialize the Gemini API response.", body.Head, statusCode, ex);
                }

                if (enumerator.Current is not { } chunk)
                {
                    continue;
                }

                if (FindError(chunk.ExtensionData) is { } error)
                {
                    throw GeminiErrorParser.FromErrorElement(error, error.GetRawText());
                }

                yield return chunk;
            }
        }
    }

    /// <summary>
    /// A gateway may report a failure as an <c>error</c> event in a shape of its own, which would otherwise parse as an empty chunk.
    /// </summary>
    private static GenerateContentResponse? ParseEvent(string eventType, ReadOnlySpan<byte> data, HttpStatusCode statusCode)
    {
        if (eventType == "error")
        {
            throw GeminiErrorParser.FromUnusableBody("The stream reported an error event.", ToBoundedString(data), statusCode);
        }

        return data.IsEmpty || data.SequenceEqual("[DONE]"u8) ? null : ParseChunk(data, statusCode);
    }

    private static JsonElement? FindError(IDictionary<string, JsonElement>? extensionData) =>
        extensionData is not null &&
        extensionData.TryGetValue("error", out var error) &&
        error.ValueKind != JsonValueKind.Null
            ? error
            : null;

    /// <summary>
    /// An event stream ignores what is not an event, such as a plain JSON error sent after keep-alive comments, so the
    /// body of a response without events starts at its first JSON line.
    /// </summary>
    private static string SkipToJson(string body)
    {
        var lines = body.Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimStart() is ['{' or '[', ..]);
        return start > 0 ? string.Join('\n', lines[start..]) : body;
    }

    private static GenerateContentResponse ParseChunk(ReadOnlySpan<byte> utf8Json, HttpStatusCode statusCode) =>
        Parse(utf8Json, GeminiJsonContext.Default.GenerateContentResponse, static response => response.ExtensionData, statusCode);

    private static string ToBoundedString(ReadOnlySpan<byte> utf8Bytes) =>
        Encoding.UTF8.GetString(utf8Bytes[..Math.Min(utf8Bytes.Length, GeminiErrorParser.MaxErrorBodyChars)]).TrimEnd();
}
