using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GeminiAPIWrapper.Options;

/// <summary>
/// Connection settings for the Gemini API (Generative Language API), including how credentials are sent.
/// <para>
/// Every header is configurable, so the client works unchanged behind API gateways and proxies
/// such as Azure API Management, Cloudflare AI Gateway, or a custom reverse proxy.
/// </para>
/// </summary>
[DebuggerDisplay("EndpointBase = {EndpointBase}, KeyHeader = {KeyHeader}, StreamingFormat = {StreamingFormat}")]
public record GenerativeLanguageOptions
{
    /// <summary>
    /// The default header used to send <see cref="ApiKey"/>: <c>x-goog-api-key</c>.
    /// </summary>
    public const string DefaultKeyHeader = "x-goog-api-key";

    /// <summary>
    /// The default timeout applied to internally created <see cref="HttpClient"/> instances (5 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Required. Model endpoint without the operation suffix, for example
    /// <c>https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash</c>.
    /// <para>
    /// Do not include <c>:generateContent</c> or <c>:streamGenerateContent</c>; the client appends the operation name automatically.
    /// Query parameters on <see cref="EndpointBase"/> (such as <c>?api-version=...</c>) are preserved.
    /// </para>
    /// </summary>
    public required string EndpointBase { get; init; }

    /// <summary>
    /// Optional. The credential sent in <see cref="KeyHeader"/>: a Gemini API key, or a gateway/proxy credential.
    /// <para>
    /// When <c>null</c> or empty, no key header is sent. Use this when the gateway injects the Gemini key itself
    /// and authenticates callers by other means (for example via <see cref="AdditionalHeaders"/>).
    /// </para>
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Optional. Name of the header that carries <see cref="ApiKey"/>. Defaults to <c>x-goog-api-key</c>.
    /// <para>
    /// Examples: <c>Ocp-Apim-Subscription-Key</c> (Azure API Management), <c>Authorization</c> (bearer-token proxies).
    /// </para>
    /// </summary>
    public string KeyHeader { get; init; } = DefaultKeyHeader;

    /// <summary>
    /// Optional. Text prepended to <see cref="ApiKey"/> in the header value, for example <c>"Bearer "</c>.
    /// </summary>
    public string? KeyPrefix { get; init; }

    /// <summary>
    /// Optional. Extra request headers sent with every call (for example gateway authentication, routing, or tracing headers).
    /// Must not duplicate <see cref="KeyHeader"/> (when <see cref="ApiKey"/> is set) or contain content headers such as <c>Content-Type</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AdditionalHeaders { get; init; }

    /// <summary>
    /// Optional. Request timeout applied when <c>GeminiAPIWrapper</c> creates its own internal <see cref="HttpClient"/>.
    /// Defaults to <see cref="DefaultTimeout"/> (5 minutes) to accommodate long-running thinking models.
    /// Ignored when an external <see cref="HttpClient"/> is supplied to <see cref="GeminiServiceBuilder.Build(GenerativeLanguageOptions, HttpClient)"/>.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Optional. When <c>false</c> (default), <c>http://</c> endpoints are only permitted for loopback hosts (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>)
    /// to prevent accidentally transmitting API keys or tokens in cleartext. Set to <c>true</c> to allow plain HTTP to non-loopback hosts.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>
    /// Optional. Wire format used by <c>:streamGenerateContent</c>.
    /// Defaults to <see cref="GeminiStreamingFormat.ServerSentEvents"/> (<c>?alt=sse</c>), matching the official Gemini SDKs.
    /// </summary>
    public GeminiStreamingFormat StreamingFormat { get; init; } = GeminiStreamingFormat.ServerSentEvents;

    /// <summary>
    /// Optional. Custom <see cref="System.Text.Json.JsonSerializerOptions"/> used by the <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// implementation when serializing custom POCO tool results (<see cref="Microsoft.Extensions.AI.FunctionResultContent.Result"/>).
    /// In NativeAOT applications, supply options backed by your <c>JsonSerializerContext</c> if tools return custom types.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; init; }

    internal string ContentType { get; } = "application/json";

    /// <summary>
    /// Formats option members while redacting <see cref="ApiKey"/> and <see cref="AdditionalHeaders"/> values
    /// so credentials are never leaked through <c>ToString()</c> or structured logging.
    /// </summary>
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append("EndpointBase = ").Append(EndpointBase);
        builder.Append(", KeyHeader = ").Append(KeyHeader);
        builder.Append(", ApiKey = ").Append(string.IsNullOrEmpty(ApiKey) ? "<none>" : "***");
        builder.Append(", KeyPrefix = ").Append(KeyPrefix);
        builder.Append(", AdditionalHeaders = [");
        if (AdditionalHeaders is { Count: > 0 })
        {
            builder.Append(string.Join(", ", AdditionalHeaders.Keys));
        }

        builder.Append("] (values redacted)");
        builder.Append(", Timeout = ").Append(Timeout ?? DefaultTimeout);
        builder.Append(", AllowInsecureHttp = ").Append(AllowInsecureHttp);
        builder.Append(", StreamingFormat = ").Append(StreamingFormat);
        return true;
    }
}

/// <summary>
/// Wire format requested from the Gemini <c>:streamGenerateContent</c> endpoint.
/// </summary>
public enum GeminiStreamingFormat
{
    /// <summary>
    /// Server-Sent Events (<c>?alt=sse</c>) where each chunk is emitted as a <c>data: {json}</c> line.
    /// Recommended for API gateways and reverse proxies because events flush immediately without array buffering.
    /// </summary>
    ServerSentEvents,

    /// <summary>
    /// Chunked JSON array (<c>[{...}, {...}]</c>) without <c>alt=sse</c>.
    /// </summary>
    JsonArray
}
