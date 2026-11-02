using System.Diagnostics;
using System.Text;

namespace GeminiAPIWrapper;

/// <summary>
/// Connection settings for <see cref="GeminiClient"/>: endpoint, default model, credentials, and transport behavior.
/// <para>
/// Every header is configurable, so the client works unchanged behind API gateways and proxies
/// such as Azure API Management, Cloudflare AI Gateway, or a custom reverse proxy.
/// </para>
/// <para>
/// All members are settable so the type can be bound from configuration
/// (<c>services.AddOptions&lt;GeminiClientOptions&gt;().BindConfiguration("Gemini")</c>).
/// <see cref="GeminiClient"/> takes an immutable, validated snapshot when it is constructed, so later changes do not affect existing clients.
/// </para>
/// </summary>
[DebuggerDisplay("Endpoint = {Endpoint}, Model = {Model}, ApiKeyHeader = {ApiKeyHeader}")]
public sealed class GeminiClientOptions
{
    /// <summary>
    /// The default header used to send <see cref="ApiKey"/>: <c>x-goog-api-key</c>.
    /// </summary>
    public const string DefaultApiKeyHeader = "x-goog-api-key";

    /// <summary>
    /// The Gemini Developer API endpoint: <c>https://generativelanguage.googleapis.com/v1beta</c>.
    /// </summary>
    public static Uri DefaultEndpoint { get; } = new("https://generativelanguage.googleapis.com/v1beta");

    /// <summary>
    /// The default request timeout (5 minutes), sized for long-running thinking models.
    /// </summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Base URI that model resources are resolved against. Requests are sent to
    /// <c>{Endpoint}/models/{model}:generateContent</c> (or <c>:streamGenerateContent</c>); query strings are preserved.
    /// Defaults to <see cref="DefaultEndpoint"/>.
    /// <para>
    /// Examples: <c>https://contoso.azure-api.net/gemini/v1beta</c> (Azure API Management),
    /// <c>https://gateway.ai.cloudflare.com/v1/{account}/{gateway}/google-ai-studio/v1beta</c> (Cloudflare AI Gateway),
    /// <c>https://aiplatform.googleapis.com/v1/projects/{project}/locations/global/publishers/google</c> (Vertex AI).
    /// </para>
    /// </summary>
    public Uri Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// Default model used when a call does not specify one, for example <c>gemini-2.5-flash</c>.
    /// Bare IDs are resolved as <c>models/{id}</c>; resource names containing <c>/</c> (such as <c>tunedModels/my-model</c>) are used as-is.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// The credential sent in <see cref="ApiKeyHeader"/>: a Gemini API key, or a gateway/proxy credential.
    /// <para>
    /// When <c>null</c> or empty (and <see cref="ApiKeyProvider"/> is not set), no key header is sent. Use this when the gateway
    /// injects the Gemini key itself and authenticates callers by other means (for example via <see cref="AdditionalHeaders"/>).
    /// </para>
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Asynchronously supplies the credential for every request (for example a short-lived OAuth access token or gateway JWT).
    /// Mutually exclusive with <see cref="ApiKey"/>. The value is combined with <see cref="ApiKeyPrefix"/> and sent in <see cref="ApiKeyHeader"/>.
    /// Implementations should cache tokens; the delegate is invoked once per request.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? ApiKeyProvider { get; set; }

    /// <summary>
    /// Name of the header that carries the credential. Defaults to <see cref="DefaultApiKeyHeader"/>.
    /// <para>
    /// Examples: <c>Ocp-Apim-Subscription-Key</c> (Azure API Management), <c>Authorization</c> (bearer-token proxies).
    /// </para>
    /// </summary>
    public string ApiKeyHeader { get; set; } = DefaultApiKeyHeader;

    /// <summary>
    /// Text prepended to the credential in the header value, for example <c>"Bearer "</c>.
    /// </summary>
    public string? ApiKeyPrefix { get; set; }

    /// <summary>
    /// Extra request headers sent with every call (for example gateway authentication, routing, or tracing headers).
    /// Must not duplicate <see cref="ApiKeyHeader"/> (when a credential is configured) or contain content headers such as <c>Content-Type</c>.
    /// </summary>
    public IDictionary<string, string> AdditionalHeaders { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Time allowed until response headers arrive. Applied to the <see cref="HttpClient"/> created internally and to clients
    /// created by <c>AddGeminiClient</c>; ignored when you pass your own <see cref="HttpClient"/>.
    /// Must be positive or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>. Defaults to <see cref="DefaultTimeout"/>.
    /// </summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>
    /// Maximum time to wait for the next chunk of a streaming response before failing with <see cref="TimeoutException"/>.
    /// <c>null</c> (default) disables the idle timeout. Must be positive or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> when set.
    /// </summary>
    public TimeSpan? StreamIdleTimeout { get; set; }

    /// <summary>
    /// When <c>false</c> (default), <c>http://</c> endpoints are only permitted for loopback hosts (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>)
    /// to prevent accidentally transmitting credentials in cleartext. Set to <c>true</c> to allow plain HTTP to non-loopback hosts.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Wire format used by <c>:streamGenerateContent</c>.
    /// Defaults to <see cref="GeminiStreamingFormat.ServerSentEvents"/> (<c>?alt=sse</c>), matching the official Gemini SDKs.
    /// </summary>
    public GeminiStreamingFormat StreamingFormat { get; set; } = GeminiStreamingFormat.ServerSentEvents;

    /// <summary>
    /// Formats option members while redacting <see cref="ApiKey"/> and <see cref="AdditionalHeaders"/> values
    /// so credentials are never leaked through <c>ToString()</c> or structured logging.
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder("GeminiClientOptions { ");
        builder.Append("Endpoint = ").Append(Endpoint);
        builder.Append(", Model = ").Append(Model);
        builder.Append(", ApiKeyHeader = ").Append(ApiKeyHeader);
        builder.Append(", ApiKey = ").Append(
            !string.IsNullOrEmpty(ApiKey) ? "***" : ApiKeyProvider is not null ? "<provider>" : "<none>");
        builder.Append(", ApiKeyPrefix = ").Append(ApiKeyPrefix);
        builder.Append(", AdditionalHeaders = [");
        if (AdditionalHeaders is { Count: > 0 })
        {
            builder.Append(string.Join(", ", AdditionalHeaders.Keys));
        }

        builder.Append("] (values redacted)");
        builder.Append(", Timeout = ").Append(Timeout);
        builder.Append(", StreamIdleTimeout = ").Append(StreamIdleTimeout?.ToString() ?? "<none>");
        builder.Append(", AllowInsecureHttp = ").Append(AllowInsecureHttp);
        builder.Append(", StreamingFormat = ").Append(StreamingFormat);
        return builder.Append(" }").ToString();
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
