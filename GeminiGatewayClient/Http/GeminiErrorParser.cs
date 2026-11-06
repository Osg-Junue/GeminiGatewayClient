using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GeminiGatewayClient.Http;

/// <summary>
/// Converts Google RPC error payloads (<c>{"error": {"code", "message", "status", "details"}}</c>) and HTTP error responses
/// into <see cref="GeminiApiException"/> instances.
/// </summary>
internal static class GeminiErrorParser
{
    /// <summary>
    /// Maximum length of an error body retained in <see cref="GeminiApiException.ErrorContent"/>.
    /// </summary>
    public const int MaxErrorBodyChars = 64 * 1024;

    /// <summary>
    /// Builds an exception from a non-success HTTP response. A body that cannot be read in time never hides the status.
    /// </summary>
    public static async Task<GeminiApiException> FromResponseAsync(HttpResponseMessage response, TimeSpan? readTimeout, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(response.Content, readTimeout, cancellationToken).ConfigureAwait(false);
        var details = ParseBody(body);

        return new GeminiApiException(
            "API request failed",
            response.StatusCode,
            body?.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] : body,
            details.Status,
            details.Message,
            details.RetryDelay ?? GetRetryAfterHeader(response),
            errorCode: details.Code);
    }

    /// <summary>
    /// Builds an exception from an <c>"error"</c> object sent with a 2xx status.
    /// </summary>
    public static GeminiApiException FromErrorElement(JsonElement error, string rawBody, HttpStatusCode httpStatusCode)
    {
        var details = ParseErrorObject(error);

        // The HTTP status of such a response says nothing about the failure, and a 2xx status must never be reported as one.
        var statusCode = details.Code is >= 400 and <= 599 ? (HttpStatusCode)details.Code.Value
            : (int)httpStatusCode >= 400 ? httpStatusCode
            : HttpStatusCode.InternalServerError;

        return new GeminiApiException(
            "The Gemini API reported an error in the response body",
            statusCode,
            rawBody,
            details.Status,
            details.Message,
            details.RetryDelay,
            errorCode: details.Code);
    }

    private static async Task<string?> ReadBodyAsync(HttpContent content, TimeSpan? readTimeout, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (readTimeout is { } limit)
        {
            timeout.CancelAfter(limit);
        }

        try
        {
            // Gemini speaks UTF-8; a gateway's charset parameter may be wrong or unknown to .NET.
            var bytes = await content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static ErrorDetails ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            // Google's streaming endpoints wrap an error in a single-element array.
            var root = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
                ? document.RootElement[0]
                : document.RootElement;

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
        // A gateway may send a text code such as "invalid_api_key".
        int? code = error.TryGetProperty("code", out var codeProperty) &&
                    codeProperty.ValueKind == JsonValueKind.Number &&
                    codeProperty.TryGetInt32(out var parsedCode)
            ? parsedCode
            : null;
        TimeSpan? retryDelay = null;

        if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind == JsonValueKind.Object &&
                    detail.TryGetProperty("retryDelay", out var delay) &&
                    TryParseSeconds(delay, out var parsedDelay))
                {
                    retryDelay = parsedDelay;
                    break;
                }
            }
        }

        return new ErrorDetails(code, GetString(error, "status"), GetString(error, "message"), retryDelay);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    /// <summary>
    /// Parses the seconds form Google uses for a protobuf <c>Duration</c>, such as <c>"37s"</c> or <c>"1.5s"</c>.
    /// </summary>
    private static bool TryParseSeconds(JsonElement value, out TimeSpan duration)
    {
        duration = default;
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 1 } text &&
            text.EndsWith('s') &&
            double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 0 and < 1e9)
        {
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return false;
    }

    private static TimeSpan? GetRetryAfterHeader(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } when delta > TimeSpan.Zero => delta,
            { Date: { } date } when date > DateTimeOffset.UtcNow => date - DateTimeOffset.UtcNow,
            _ => null
        };

    private readonly record struct ErrorDetails(int? Code, string? Status, string? Message, TimeSpan? RetryDelay);
}
