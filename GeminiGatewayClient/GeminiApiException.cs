using System.Collections.Frozen;
using System.Net;

namespace GeminiGatewayClient;

/// <summary>
/// Exception thrown when a Gemini API call returns a non-success HTTP status code
/// or returns a payload that cannot be deserialized.
/// </summary>
public sealed class GeminiApiException : Exception
{
    private static readonly FrozenSet<string> s_transientRpcStatuses = new[]
    {
        "RESOURCE_EXHAUSTED",
        "UNAVAILABLE",
        "DEADLINE_EXCEEDED",
        "INTERNAL"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="GeminiApiException"/> with a default message.
    /// </summary>
    public GeminiApiException()
        : base("Gemini API request failed.")
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="GeminiApiException"/> with the specified error message.
    /// </summary>
    /// <param name="message">High-level error description.</param>
    public GeminiApiException(string? message)
        : base(message ?? "Gemini API request failed.")
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="GeminiApiException"/> with the specified error message and inner exception.
    /// </summary>
    /// <param name="message">High-level error description.</param>
    /// <param name="innerException">Underlying exception, if any.</param>
    public GeminiApiException(string? message, Exception? innerException)
        : base(message ?? "Gemini API request failed.", innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="GeminiApiException"/>.
    /// </summary>
    /// <param name="message">High-level error description.</param>
    /// <param name="statusCode">HTTP status code returned by the endpoint or gateway.</param>
    /// <param name="errorContent">Raw response body returned by the server (truncated to 64 KB), if available.</param>
    /// <param name="innerException">Underlying exception, if any.</param>
    public GeminiApiException(
        string message,
        HttpStatusCode statusCode,
        string? errorContent = null,
        Exception? innerException = null)
        : this(message, statusCode, errorContent, errorStatus: null, errorMessage: null, retryAfter: null, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="GeminiApiException"/> with parsed Google RPC error details.
    /// </summary>
    /// <param name="message">High-level error description.</param>
    /// <param name="statusCode">HTTP status code returned by the endpoint or gateway.</param>
    /// <param name="errorContent">Raw response body returned by the server (truncated to 64 KB), if available.</param>
    /// <param name="errorStatus">Canonical Google RPC status string (e.g. <c>RESOURCE_EXHAUSTED</c>, <c>INVALID_ARGUMENT</c>), if present.</param>
    /// <param name="errorMessage">Parsed <c>error.message</c> from the JSON error payload, if present.</param>
    /// <param name="retryAfter">Suggested retry delay parsed from <c>RetryInfo.retryDelay</c> or the HTTP <c>Retry-After</c> header, if present.</param>
    /// <param name="innerException">Underlying exception, if any.</param>
    /// <param name="errorCode">Numeric <c>error.code</c> from the Google RPC error payload, if present.</param>
    public GeminiApiException(
        string message,
        HttpStatusCode statusCode,
        string? errorContent,
        string? errorStatus,
        string? errorMessage,
        TimeSpan? retryAfter,
        Exception? innerException = null,
        int? errorCode = null)
        : base(FormatExceptionMessage(message, statusCode, errorStatus, errorMessage), innerException)
    {
        StatusCode = statusCode;
        ErrorContent = errorContent;
        ErrorCode = errorCode;
        ErrorStatus = errorStatus;
        ErrorMessage = errorMessage;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// HTTP status code returned by the endpoint or gateway.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Raw error response body returned by the server (up to 64 KB), if available.
    /// </summary>
    public string? ErrorContent { get; }

    /// <summary>
    /// Numeric error code from <c>error.code</c> in the JSON error response (HTTP status code or gRPC canonical code), if available.
    /// </summary>
    public int? ErrorCode { get; }

    /// <summary>
    /// Canonical Google RPC status code from <c>error.status</c> (for example <c>RESOURCE_EXHAUSTED</c> or <c>INVALID_ARGUMENT</c>), if available.
    /// </summary>
    public string? ErrorStatus { get; }

    /// <summary>
    /// Human-readable error message from <c>error.message</c> in the JSON error response, if available.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Recommended delay before retrying the request, extracted from <c>google.rpc.RetryInfo</c> or the HTTP <c>Retry-After</c> header when available.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// <c>true</c> when the failure is typically transient and the request may succeed if retried
    /// (HTTP 408, 429, 500, 502, 503, or 504, or a transient Google RPC status such as <c>RESOURCE_EXHAUSTED</c>,
    /// <c>UNAVAILABLE</c>, <c>DEADLINE_EXCEEDED</c>, or <c>INTERNAL</c>). Honor <see cref="RetryAfter"/> when present.
    /// </summary>
    public bool IsTransient =>
        StatusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
        || IsTransientRpcStatus(ErrorStatus);

    private static bool IsTransientRpcStatus(string? status) =>
        status is not null && s_transientRpcStatuses.Contains(status);

    private static string FormatExceptionMessage(
        string message,
        HttpStatusCode statusCode,
        string? errorStatus,
        string? errorMessage)
    {
        var statusPart = string.IsNullOrWhiteSpace(errorStatus)
            ? $"StatusCode: {(int)statusCode}"
            : $"StatusCode: {(int)statusCode} {errorStatus}";

        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return $"{message} ({statusPart})";
        }

        const int maxDetailLength = 512;
        var detail = errorMessage.Length <= maxDetailLength
            ? errorMessage
            : string.Concat(errorMessage.AsSpan(0, maxDetailLength), "...");

        return $"{message} ({statusPart}): {detail}";
    }
}
