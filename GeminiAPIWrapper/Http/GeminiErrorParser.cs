using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GeminiAPIWrapper.Http;

/// <summary>
/// Converts Google RPC error payloads (<c>{"error": {"code", "message", "status", "details"}}</c>) and HTTP error responses
/// into <see cref="GeminiApiException"/> instances.
/// </summary>
internal static class GeminiErrorParser
{
    /// <summary>
    /// Maximum number of characters of an error body retained in <see cref="GeminiApiException.ErrorContent"/>.
    /// </summary>
    public const int MaxErrorBodyChars = 64 * 1024;

    /// <summary>
    /// Builds an exception from a non-success HTTP response, reading at most <see cref="MaxErrorBodyChars"/> of its body.
    /// </summary>
    public static async Task<GeminiApiException> CreateFromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var details = Parse(body);
        var retryAfter = details.RetryDelay ?? GetRetryAfterHeader(response);

        return new GeminiApiException(
            "API request failed",
            response.StatusCode,
            body,
            details.Status,
            details.Message,
            retryAfter);
    }

    /// <summary>
    /// Returns an exception when <paramref name="payload"/> is an in-stream error object (sent after the HTTP 200 status line),
    /// or <c>null</c> when it is a regular chunk.
    /// </summary>
    public static GeminiApiException? TryCreateFromStreamPayload(JsonElement payload, HttpStatusCode httpStatusCode)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var raw = payload.GetRawText();
        if (raw.Length > MaxErrorBodyChars)
        {
            raw = raw[..MaxErrorBodyChars];
        }

        var details = ParseErrorObject(error);
        var statusCode = MapStatusCode(details.Code, details.Status, httpStatusCode);

        return new GeminiApiException(
            "The Gemini API reported an error in the response stream",
            statusCode,
            raw,
            details.Status,
            details.Message,
            details.RetryDelay);
    }

    private static HttpStatusCode MapStatusCode(int? code, string? rpcStatus, HttpStatusCode fallback)
    {
        if (code is >= 100 and <= 599)
        {
            return (HttpStatusCode)code.Value;
        }

        // Map canonical gRPC status codes (1..16) when returned in-stream instead of an HTTP status code.
        switch (code)
        {
            case 3:
            case 9:
            case 11:
                return HttpStatusCode.BadRequest;
            case 4:
                return HttpStatusCode.GatewayTimeout;
            case 5:
                return HttpStatusCode.NotFound;
            case 6:
            case 10:
                return HttpStatusCode.Conflict;
            case 7:
                return HttpStatusCode.Forbidden;
            case 8:
                return HttpStatusCode.TooManyRequests;
            case 12:
                return HttpStatusCode.NotImplemented;
            case 13:
                return HttpStatusCode.InternalServerError;
            case 14:
                return HttpStatusCode.ServiceUnavailable;
            case 16:
                return HttpStatusCode.Unauthorized;
        }

        if (!string.IsNullOrEmpty(rpcStatus))
        {
            if (rpcStatus.Equals("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
            {
                return HttpStatusCode.TooManyRequests;
            }

            if (rpcStatus.Equals("UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
            {
                return HttpStatusCode.ServiceUnavailable;
            }

            if (rpcStatus.Equals("DEADLINE_EXCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                return HttpStatusCode.GatewayTimeout;
            }

            if (rpcStatus.Equals("INTERNAL", StringComparison.OrdinalIgnoreCase))
            {
                return HttpStatusCode.InternalServerError;
            }
        }

        return fallback;
    }

    private static ErrorDetails Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            // Streaming endpoints may wrap the error in a single-element array.
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object
                ? ParseErrorObject(error)
                : default;
        }
        catch (JsonException)
        {
            // Non-JSON or truncated error body (for example an HTML page from a gateway); keep the raw body only.
            return default;
        }
    }

    private static ErrorDetails ParseErrorObject(JsonElement error)
    {
        int? code = error.TryGetProperty("code", out var codeProp) &&
                    codeProp.ValueKind == JsonValueKind.Number &&
                    codeProp.TryGetInt32(out var parsedCode)
            ? parsedCode
            : null;

        var status = error.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String
            ? statusProp.GetString()
            : null;

        var message = error.TryGetProperty("message", out var messageProp) && messageProp.ValueKind == JsonValueKind.String
            ? messageProp.GetString()
            : null;

        TimeSpan? retryDelay = null;
        if (error.TryGetProperty("details", out var detailsProp) && detailsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in detailsProp.EnumerateArray())
            {
                if (detail.ValueKind == JsonValueKind.Object &&
                    detail.TryGetProperty("retryDelay", out var delayProp) &&
                    delayProp.ValueKind == JsonValueKind.String &&
                    TryParseProtobufDuration(delayProp.GetString(), out var delay))
                {
                    retryDelay = delay;
                    break;
                }
            }
        }

        return new ErrorDetails(code, status, message, retryDelay);
    }

    private static TimeSpan? GetRetryAfterHeader(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter.Date is { } date && date - DateTimeOffset.UtcNow is var diff && diff > TimeSpan.Zero)
        {
            return diff;
        }

        return null;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

            var buffer = ArrayPool<char>.Shared.Rent(4096);
            try
            {
                var builder = new StringBuilder();
                var remaining = MaxErrorBodyChars;

                while (remaining > 0)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    builder.Append(buffer, 0, read);
                    remaining -= read;
                }

                return builder.ToString();
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Parses a protobuf JSON <c>Duration</c> such as <c>"37s"</c>, <c>"1.5s"</c>, or <c>"250ms"</c>.
    /// </summary>
    internal static bool TryParseProtobufDuration(string? value, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        double scale;
        if (span.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^2];
            scale = 1.0;
        }
        else if (span.EndsWith("us", StringComparison.OrdinalIgnoreCase) ||
                 span.EndsWith("µs", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^2];
            scale = 0.001;
        }
        else if (span.EndsWith("ns", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^2];
            scale = 0.000001;
        }
        else if (span.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^1];
            scale = 1000.0;
        }
        else
        {
            return false;
        }

        if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ||
            !double.IsFinite(amount) ||
            amount < 0)
        {
            return false;
        }

        var milliseconds = amount * scale;
        if (milliseconds >= TimeSpan.MaxValue.TotalMilliseconds)
        {
            return false;
        }

        duration = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private readonly record struct ErrorDetails(int? Code, string? Status, string? Message, TimeSpan? RetryDelay);
}
