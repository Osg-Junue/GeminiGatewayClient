namespace GeminiGatewayClient.Http;

/// <summary>
/// Immutable snapshot of <see cref="GeminiClientOptions"/> used by <see cref="GeminiClient"/>.
/// Separating the mutable, bindable options type from this snapshot guarantees a constructed client is never affected by later option changes.
/// </summary>
internal sealed class GeminiClientSettings
{
    private GeminiClientSettings(
        GeminiEndpoint endpoint,
        GeminiRequestHeaders headers,
        TimeSpan? streamIdleTimeout,
        bool useServerSentEvents)
    {
        Endpoint = endpoint;
        Headers = headers;
        StreamIdleTimeout = streamIdleTimeout;
        UseServerSentEvents = useServerSentEvents;
    }

    public GeminiEndpoint Endpoint { get; }

    public GeminiRequestHeaders Headers { get; }

    public TimeSpan? StreamIdleTimeout { get; }

    public bool UseServerSentEvents { get; }

    /// <summary>
    /// Captures an immutable snapshot of <paramref name="options"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The options cannot work.</exception>
    public static GeminiClientSettings Create(GeminiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new GeminiClientSettings(
            GeminiEndpoint.Create(options.Endpoint, options.Model, options.AllowInsecureHttp, options.EmbeddingModel),
            GeminiRequestHeaders.Create(options),
            options.StreamIdleTimeout,
            options.UseServerSentEvents);
    }
}
