using System.Diagnostics;
using System.Text;

namespace GeminiGatewayClient;

/// <summary>
/// Connection settings for <see cref="GeminiClient"/>: endpoint, default model, credentials, and transport behavior.
/// <para>
/// Every header is configurable, so the client works unchanged behind API gateways and proxies
/// such as Azure API Management, Cloudflare AI Gateway, or a custom reverse proxy.
/// </para>
/// <para>
/// All members are settable so the type can be bound from configuration
/// (<c>services.AddOptions&lt;GeminiClientOptions&gt;().BindConfiguration("Gemini")</c>).
/// <see cref="GeminiClient"/> takes an immutable snapshot when it is constructed, so later changes do not affect existing clients.
/// </para>
/// </summary>
[DebuggerDisplay("Endpoint = {RedactedEndpoint}, Model = {Model}, EmbeddingModel = {EmbeddingModel}, ApiKeyHeader = {ApiKeyHeader}")]
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
    /// The default streaming idle timeout (2 minutes).
    /// </summary>
    public static TimeSpan DefaultStreamIdleTimeout { get; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Base URI that model resources are resolved against. Requests are sent to
    /// <c>{Endpoint}/models/{model}:generateContent</c> (or <c>:streamGenerateContent</c>, <c>:embedContent</c>, <c>:batchEmbedContents</c>); query strings are preserved.
    /// Defaults to <see cref="DefaultEndpoint"/>.
    /// <para>
    /// Examples: <c>https://contoso.azure-api.net/gemini/v1beta</c> (Azure API Management),
    /// <c>https://gateway.ai.cloudflare.com/v1/{account}/{gateway}/google-ai-studio/v1beta</c> (Cloudflare AI Gateway),
    /// <c>https://aiplatform.googleapis.com/v1/projects/{project}/locations/global/publishers/google</c> (Vertex AI).
    /// </para>
    /// </summary>
    public Uri Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// Default model used when a call does not specify one, for example <c>gemini-flash-latest</c>.
    /// Bare IDs are resolved as <c>models/{id}</c>; resource names containing <c>/</c> (such as <c>tunedModels/my-model</c>) are used as-is.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Optional default embedding model used by <see cref="IGeminiClient.EmbedContentAsync"/>, <see cref="IGeminiClient.BatchEmbedContentsAsync"/>,
    /// and the <c>Microsoft.Extensions.AI</c> <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> adapter when a call does not specify
    /// a model (for example <c>gemini-embedding-001</c>). When <c>null</c>, falls back to <see cref="Model"/>.
    /// </summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// The credential sent in <see cref="ApiKeyHeader"/>: a Gemini API key, or a gateway/proxy credential.
    /// <para>
    /// When <c>null</c> or empty (and <see cref="ApiKeyProvider"/> is not set), no key header is sent. Use this when the gateway
    /// injects the Gemini key itself and authenticates callers by other means (for example via <see cref="AdditionalHeaders"/>).
    /// </para>
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Asynchronously supplies the credential for every client call (for example a short-lived OAuth access token or gateway JWT).
    /// Mutually exclusive with <see cref="ApiKey"/>. The value is combined with <see cref="ApiKeyPrefix"/> and sent in <see cref="ApiKeyHeader"/>.
    /// Implementations should cache tokens; the delegate is invoked once per client call, so retries inside an HTTP handler resend the same value.
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
    /// Content headers such as <c>Content-Type</c> cannot be set here.
    /// </summary>
    public IDictionary<string, string> AdditionalHeaders { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum time to wait for more data on a streaming response before failing with <see cref="TimeoutException"/>.
    /// Any received data resets it, including keep-alive comments from a gateway.
    /// Defaults to <see cref="DefaultStreamIdleTimeout"/> (2 minutes). Set to <c>null</c> or <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable.
    /// </summary>
    public TimeSpan? StreamIdleTimeout { get; set; } = DefaultStreamIdleTimeout;

    /// <summary>
    /// Whether streaming calls ask for Server-Sent Events with <c>alt=sse</c>. Defaults to <c>true</c>. Set to <c>false</c> for a
    /// gateway that rejects the query string: Gemini then answers with a JSON array, which is read as it arrives, though a complete
    /// chunk may wait for the data after it.
    /// </summary>
    public bool UseServerSentEvents { get; set; } = true;

    /// <summary>
    /// When <c>false</c> (default), <c>http://</c> endpoints are only permitted for loopback hosts (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>)
    /// to prevent accidentally transmitting credentials in cleartext. Set to <c>true</c> to allow plain HTTP to non-loopback hosts.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    internal string RedactedEndpoint => FormatEndpoint(Endpoint);

    /// <summary>
    /// Formats option members while redacting <see cref="ApiKey"/>, <see cref="AdditionalHeaders"/> values, and the whole
    /// <see cref="Endpoint"/> query string so credentials are never leaked through <c>ToString()</c> or structured logging.
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder("GeminiClientOptions { ");
        builder.Append("Endpoint = ").Append(RedactedEndpoint);
        builder.Append(", Model = ").Append(Model);
        if (!string.IsNullOrWhiteSpace(EmbeddingModel))
        {
            builder.Append(", EmbeddingModel = ").Append(EmbeddingModel);
        }

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
        builder.Append(", StreamIdleTimeout = ").Append(StreamIdleTimeout?.ToString() ?? "<none>");
        builder.Append(", AllowInsecureHttp = ").Append(AllowInsecureHttp);
        return builder.Append(" }").ToString();
    }

    /// <summary>
    /// Gateways often carry keys in the query string or user info, so only the scheme, host, and path are shown.
    /// </summary>
    private static string FormatEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            return "<relative>";
        }

        var address = endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        return endpoint.Query.Length > 0 ? address + "?***" : address;
    }
}
