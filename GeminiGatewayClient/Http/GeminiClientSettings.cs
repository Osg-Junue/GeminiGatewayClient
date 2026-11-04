using System.Text.Json;

namespace GeminiGatewayClient.Http;

/// <summary>
/// Immutable, validated snapshot of <see cref="GeminiClientOptions"/> used by <see cref="GeminiClient"/>.
/// Separating the mutable, bindable options type from this snapshot guarantees a constructed client is never affected by later option changes.
/// </summary>
internal sealed class GeminiClientSettings
{
    private GeminiClientSettings(
        GeminiEndpoint endpoint,
        string? defaultEmbeddingModel,
        GeminiRequestHeaders headers,
        TimeSpan timeout,
        TimeSpan? streamIdleTimeout,
        GeminiStreamingFormat streamingFormat,
        JsonSerializerOptions? toolSerializerOptions)
    {
        Endpoint = endpoint;
        DefaultEmbeddingModel = defaultEmbeddingModel;
        Headers = headers;
        Timeout = timeout;
        StreamIdleTimeout = streamIdleTimeout;
        StreamingFormat = streamingFormat;
        ToolSerializerOptions = toolSerializerOptions;
    }

    public GeminiEndpoint Endpoint { get; }

    public string? DefaultEmbeddingModel { get; }

    public GeminiRequestHeaders Headers { get; }

    public TimeSpan Timeout { get; }

    public TimeSpan? StreamIdleTimeout { get; }

    public GeminiStreamingFormat StreamingFormat { get; }

    public JsonSerializerOptions? ToolSerializerOptions { get; }

    /// <summary>
    /// Validates cross-property invariants on <paramref name="options"/> and captures an immutable snapshot.
    /// Individual property ranges (e.g., <see cref="GeminiClientOptions.Timeout"/>, <see cref="GeminiClientOptions.StreamIdleTimeout"/>,
    /// <see cref="GeminiClientOptions.StreamingFormat"/>) are enforced eagerly by <see cref="GeminiClientOptions"/> property setters.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static GeminiClientSettings Create(GeminiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var defaultEmbeddingModel = string.IsNullOrWhiteSpace(options.EmbeddingModel) ? null : options.EmbeddingModel.Trim();

        return new GeminiClientSettings(
            GeminiEndpoint.Create(options.Endpoint, options.Model, options.AllowInsecureHttp, defaultEmbeddingModel),
            defaultEmbeddingModel,
            GeminiRequestHeaders.Create(options),
            options.Timeout,
            options.StreamIdleTimeout,
            options.StreamingFormat,
            options.ToolSerializerOptions);
    }
}
