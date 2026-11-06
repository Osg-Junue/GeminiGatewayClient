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

            if (extensionDataSelector(response) is { } extensionData &&
                extensionData.TryGetValue("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                throw GeminiErrorParser.FromErrorElement(error, ToBoundedString(utf8Json), statusCode);
            }

            return response;
        }
        catch (JsonException ex)
        {
            throw new GeminiApiException("Failed to deserialize the Gemini API response.", statusCode, ToBoundedString(utf8Json), ex);
        }
    }

    /// <summary>
    /// Reads the chunks of a <c>:streamGenerateContent</c> response. A gateway may answer with a JSON error or an HTML page
    /// whatever its <c>Content-Type</c> says, so the body itself shows whether it is an event stream.
    /// </summary>
    public static async IAsyncEnumerable<GenerateContentResponse> ReadChunksAsync(
        ResponseBodyStream body,
        HttpStatusCode statusCode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (await body.PeekAsync(cancellationToken).ConfigureAwait(false) is (byte)'{' or (byte)'[' or (byte)'<')
        {
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            yield return ParseChunk(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), statusCode);
            yield break;
        }

        var parser = SseParser.Create(body, (_, data) => data.IsEmpty || data.SequenceEqual("[DONE]"u8) ? null : ParseChunk(data, statusCode));
        var isEmpty = true;
        await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            isEmpty = false;
            if (item.Data is { } chunk)
            {
                yield return chunk;
            }
        }

        if (isEmpty)
        {
            throw new GeminiApiException("The Gemini API returned an empty streaming response.", statusCode);
        }
    }

    private static GenerateContentResponse ParseChunk(ReadOnlySpan<byte> utf8Json, HttpStatusCode statusCode) =>
        Parse(utf8Json, GeminiJsonContext.Default.GenerateContentResponse, static response => response.ExtensionData, statusCode);

    private static string ToBoundedString(ReadOnlySpan<byte> utf8Bytes) =>
        Encoding.UTF8.GetString(utf8Bytes[..Math.Min(utf8Bytes.Length, GeminiErrorParser.MaxErrorBodyChars)]).TrimEnd();
}
