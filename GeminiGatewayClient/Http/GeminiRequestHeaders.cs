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

        var headers = new List<KeyValuePair<string, string>>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var probe = new HttpRequestMessage();

        if (hasStaticKey || hasDynamicKeyProvider)
        {
            // The key from ApiKeyProvider is resolved and checked per client call.
            ValidateName(probe, options.ApiKeyHeader, nameof(GeminiClientOptions.ApiKeyHeader));
            ValidateValue(options.ApiKeyHeader, options.ApiKeyPrefix, nameof(GeminiClientOptions.ApiKeyPrefix));
            seenNames.Add(options.ApiKeyHeader);
        }

        if (hasStaticKey)
        {
            ValidateValue(options.ApiKeyHeader, options.ApiKey, nameof(GeminiClientOptions.ApiKey));
            headers.Add(new KeyValuePair<string, string>(options.ApiKeyHeader, $"{options.ApiKeyPrefix}{options.ApiKey}"));
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

                ValidateName(probe, name, nameof(GeminiClientOptions.AdditionalHeaders));
                ValidateValue(name, value, nameof(GeminiClientOptions.AdditionalHeaders));
                headers.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return new GeminiRequestHeaders([.. headers], options.ApiKeyHeader, options.ApiKeyPrefix, options.ApiKeyProvider);
    }

    /// <summary>
    /// Adds all headers to <paramref name="message"/>, resolving the API key from the provider when one is configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">The API key provider returned an empty or invalid value.</exception>
    public async ValueTask ApplyAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        foreach (var (name, value) in _staticHeaders)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (_apiKeyProvider is null)
        {
            return;
        }

        var key = await _apiKeyProvider(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key) || IsInvalidHeaderText(key, allowHorizontalTab: true))
        {
            throw new InvalidOperationException("GeminiClientOptions.ApiKeyProvider returned an empty, whitespace-only, or non-ASCII value.");
        }

        if (!message.Headers.TryAddWithoutValidation(_apiKeyHeader, $"{_apiKeyPrefix}{key}"))
        {
            throw new InvalidOperationException($"Failed to apply credential header '{_apiKeyHeader}'.");
        }
    }

    private static void ValidateName(HttpRequestMessage probe, string name, string paramName)
    {
        if (IsInvalidHeaderText(name, allowHorizontalTab: false))
        {
            throw new ArgumentException($"Header name '{name}' must contain only printable ASCII characters.", paramName);
        }

        if (!probe.Headers.TryAddWithoutValidation(name, string.Empty))
        {
            throw new ArgumentException(
                $"Header '{name}' is not a valid request header (content headers such as 'Content-Type' cannot be set via AdditionalHeaders or ApiKeyHeader).",
                paramName);
        }
    }

    /// <summary>
    /// Checks one piece of a header value. The text is left out of the message, because it may be a credential.
    /// </summary>
    private static void ValidateValue(string name, string? value, string paramName)
    {
        if (value is not null && IsInvalidHeaderText(value, allowHorizontalTab: true))
        {
            throw new ArgumentException($"The value of header '{name}' must contain only printable ASCII characters.", paramName);
        }
    }

    /// <summary>
    /// HTTP header text must be printable ASCII; anything else would only fail later, on the first request.
    /// </summary>
    private static bool IsInvalidHeaderText(string value, bool allowHorizontalTab) =>
        value.Any(c => c is < ' ' or > '~' && !(allowHorizontalTab && c == '\t'));
}
