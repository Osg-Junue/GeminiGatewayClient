namespace GeminiGatewayClient.Http;

/// <summary>
/// Validated set of request headers (credential header plus additional gateway headers) that is applied to every
/// <see cref="HttpRequestMessage"/>. Headers are attached per request so a shared <see cref="HttpClient"/> is never mutated.
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
    /// Validates the credential and additional headers configured on <paramref name="options"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A header name or value is invalid, duplicated, or conflicting.</exception>
    public static GeminiRequestHeaders Create(GeminiClientOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKeyHeader, nameof(GeminiClientOptions.ApiKeyHeader));

        if (options.ApiKey is { Length: > 0 } && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException(
                "GeminiClientOptions.ApiKey cannot be whitespace-only. Leave it null or empty when no key header should be sent.",
                nameof(GeminiClientOptions.ApiKey));
        }

        var hasStaticKey = !string.IsNullOrWhiteSpace(options.ApiKey);
        var hasDynamicKeyProvider = options.ApiKeyProvider is not null;
        if (hasStaticKey && hasDynamicKeyProvider)
        {
            throw new ArgumentException(
                "GeminiClientOptions.ApiKey and GeminiClientOptions.ApiKeyProvider are mutually exclusive.",
                nameof(GeminiClientOptions.ApiKeyProvider));
        }

        var additionalCount = options.AdditionalHeaders?.Count ?? 0;
        var headers = new List<KeyValuePair<string, string>>((hasStaticKey ? 1 : 0) + additionalCount);
        var seenNames = new HashSet<string>((hasStaticKey || hasDynamicKeyProvider ? 1 : 0) + additionalCount, StringComparer.OrdinalIgnoreCase);
        using var probe = new HttpRequestMessage();

        if (hasStaticKey)
        {
            var headerValue = $"{options.ApiKeyPrefix}{options.ApiKey}";
            Validate(probe, options.ApiKeyHeader, headerValue, nameof(GeminiClientOptions.ApiKeyHeader));
            seenNames.Add(options.ApiKeyHeader);
            headers.Add(new KeyValuePair<string, string>(options.ApiKeyHeader, headerValue));
        }
        else if (hasDynamicKeyProvider)
        {
            // Validate the header name and prefix up front; the actual key is resolved per request by ApiKeyProvider.
            Validate(probe, options.ApiKeyHeader, $"{options.ApiKeyPrefix}probe", nameof(GeminiClientOptions.ApiKeyHeader));
            seenNames.Add(options.ApiKeyHeader);
        }

        if (options.AdditionalHeaders is { } additionalHeaders)
        {
            foreach (var (name, rawValue) in additionalHeaders)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(GeminiClientOptions.AdditionalHeaders));
                var value = rawValue ?? string.Empty;

                if (!seenNames.Add(name))
                {
                    throw new ArgumentException(
                        $"Duplicate header '{name}' in AdditionalHeaders (conflicts with ApiKeyHeader or another additional header).",
                        nameof(GeminiClientOptions.AdditionalHeaders));
                }

                Validate(probe, name, value, nameof(GeminiClientOptions.AdditionalHeaders));
                headers.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return new GeminiRequestHeaders([.. headers], options.ApiKeyHeader, options.ApiKeyPrefix, options.ApiKeyProvider);
    }

    /// <summary>
    /// Adds all headers to <paramref name="message"/>, resolving the API key from the provider when one is configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">The API key provider returned an empty or invalid value.</exception>
    public ValueTask ApplyAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        foreach (var (name, value) in _staticHeaders)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        return _apiKeyProvider is null ? ValueTask.CompletedTask : ApplyDynamicKeyAsync(message, cancellationToken);
    }

    private async ValueTask ApplyDynamicKeyAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        var key = await _apiKeyProvider!(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key) || ContainsNewLine(key))
        {
            throw new InvalidOperationException("GeminiClientOptions.ApiKeyProvider returned an empty, whitespace-only, or newline-containing value.");
        }

        message.Headers.TryAddWithoutValidation(_apiKeyHeader, $"{_apiKeyPrefix}{key}");
    }

    private static void Validate(HttpRequestMessage probe, string name, string value, string paramName)
    {
        if (ContainsNewLine(name) || ContainsNewLine(value))
        {
            throw new ArgumentException($"Header '{name}' contains invalid newline characters.", paramName);
        }

        if (!probe.Headers.TryAddWithoutValidation(name, value))
        {
            throw new ArgumentException(
                $"Header '{name}' is not a valid request header (content headers such as 'Content-Type' cannot be set via AdditionalHeaders or ApiKeyHeader).",
                paramName);
        }
    }

    private static bool ContainsNewLine(string value) => value.AsSpan().IndexOfAny('\r', '\n') >= 0;
}

