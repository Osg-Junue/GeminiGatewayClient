namespace GeminiGatewayClient.Http;

/// <summary>
/// Request headers (credential header plus additional gateway headers) applied to every <see cref="HttpRequestMessage"/>.
/// Headers are attached per request so a shared <see cref="HttpClient"/> is never mutated.
/// </summary>
internal sealed class GeminiRequestHeaders
{
    private readonly KeyValuePair<string, string>[] _staticHeaders;
    private readonly string _apiKeyHeader;
    private readonly string? _apiKeyPrefix;
    private readonly Func<CancellationToken, ValueTask<string>>? _apiKeyProvider;

    private GeminiRequestHeaders(
        KeyValuePair<string, string>[] staticHeaders,
        string apiKeyHeader,
        string? apiKeyPrefix,
        Func<CancellationToken, ValueTask<string>>? apiKeyProvider)
    {
        _staticHeaders = staticHeaders;
        _apiKeyHeader = apiKeyHeader;
        _apiKeyPrefix = apiKeyPrefix;
        _apiKeyProvider = apiKeyProvider;
    }

    /// <summary>
    /// Captures the headers configured on <paramref name="options"/>. A header that cannot be sent fails here, not on the first request.
    /// </summary>
    /// <exception cref="ArgumentException">A header cannot be sent, or both a key and a key provider are set.</exception>
    public static GeminiRequestHeaders Create(GeminiClientOptions options)
    {
        var hasStaticKey = !string.IsNullOrEmpty(options.ApiKey);
        if (hasStaticKey && options.ApiKeyProvider is not null)
        {
            throw new ArgumentException(
                "GeminiClientOptions.ApiKey and GeminiClientOptions.ApiKeyProvider are mutually exclusive.",
                nameof(GeminiClientOptions.ApiKeyProvider));
        }

        KeyValuePair<string, string>[] credential = hasStaticKey ? [new(options.ApiKeyHeader, $"{options.ApiKeyPrefix}{options.ApiKey}")] : [];
        var headers = new GeminiRequestHeaders([.. credential, .. options.AdditionalHeaders], options.ApiKeyHeader, options.ApiKeyPrefix, options.ApiKeyProvider);

        using var probe = new HttpRequestMessage();
        headers.AddStaticHeaders(probe);
        if (options.ApiKeyProvider is not null)
        {
            Add(probe, options.ApiKeyHeader, options.ApiKeyPrefix ?? string.Empty);
        }

        return headers;
    }

    /// <summary>
    /// Adds all headers to <paramref name="message"/>, resolving the API key from the provider when one is configured.
    /// </summary>
    /// <exception cref="ArgumentException">The API key provider returned nothing, or a value that cannot be sent.</exception>
    public async ValueTask ApplyAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        AddStaticHeaders(message);
        if (_apiKeyProvider is not null)
        {
            var key = await _apiKeyProvider(cancellationToken).ConfigureAwait(false);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            Add(message, _apiKeyHeader, $"{_apiKeyPrefix}{key}");
        }
    }

    private void AddStaticHeaders(HttpRequestMessage message)
    {
        foreach (var (name, value) in _staticHeaders)
        {
            Add(message, name, value);
        }
    }

    /// <summary>
    /// .NET would send a line break or NUL as it is, which injects a header, and would join two values of one header, which
    /// hides a credential conflict. The value stays out of the message, because it may be a credential.
    /// </summary>
    private static void Add(HttpRequestMessage message, string name, string value)
    {
        if (value.AsSpan().ContainsAny('\r', '\n', '\0') || message.Headers.TryGetValues(name, out _) || !message.Headers.TryAddWithoutValidation(name, value))
        {
            throw new ArgumentException($"Header '{name}' cannot be sent.", nameof(name));
        }
    }
}
