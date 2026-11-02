namespace GeminiAPIWrapper.Http;

/// <summary>
/// Immutable, validated snapshot of <see cref="GeminiClientOptions"/> used by <see cref="GeminiClient"/>.
/// Separating the mutable, bindable options type from this snapshot guarantees a constructed client is never affected by later option changes.
/// </summary>
internal sealed class GeminiClientSettings
{
    private GeminiClientSettings(
        GeminiEndpoint endpoint,
        GeminiRequestHeaders headers,
        TimeSpan timeout,
        TimeSpan? streamIdleTimeout,
        GeminiStreamingFormat streamingFormat)
    {
        Endpoint = endpoint;
        Headers = headers;
        Timeout = timeout;
        StreamIdleTimeout = streamIdleTimeout;
        StreamingFormat = streamingFormat;
    }

    public GeminiEndpoint Endpoint { get; }

    public GeminiRequestHeaders Headers { get; }

    public TimeSpan Timeout { get; }

    public TimeSpan? StreamIdleTimeout { get; }

    public GeminiStreamingFormat StreamingFormat { get; }

    /// <summary>
    /// Validates <paramref name="options"/> and captures an immutable snapshot.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static GeminiClientSettings Create(GeminiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsValidTimeout(options.Timeout))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Timeout, "GeminiClientOptions.Timeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (options.StreamIdleTimeout is { } idle && !IsValidTimeout(idle))
        {
            throw new ArgumentOutOfRangeException(nameof(options), idle, "GeminiClientOptions.StreamIdleTimeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (!Enum.IsDefined(options.StreamingFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.StreamingFormat, "GeminiClientOptions.StreamingFormat is not a defined value.");
        }

        return new GeminiClientSettings(
            GeminiEndpoint.Create(options.Endpoint, options.Model, options.AllowInsecureHttp),
            GeminiRequestHeaders.Create(options),
            options.Timeout,
            options.StreamIdleTimeout,
            options.StreamingFormat);
    }

    private static bool IsValidTimeout(TimeSpan value) => value > TimeSpan.Zero || value == System.Threading.Timeout.InfiniteTimeSpan;
}

