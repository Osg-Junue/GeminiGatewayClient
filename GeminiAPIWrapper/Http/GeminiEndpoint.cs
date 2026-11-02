namespace GeminiAPIWrapper.Http;

/// <summary>
/// Immutable, validated representation of the configured endpoint that composes operation URIs such as
/// <c>{endpoint}/models/{model}:generateContent</c> while preserving any query string on the endpoint.
/// </summary>
internal sealed class GeminiEndpoint
{
    private const string ModelsSegment = "models/";

    private readonly Uri _baseUri;

    private GeminiEndpoint(Uri baseUri, string? defaultModel)
    {
        _baseUri = baseUri;
        DefaultModel = defaultModel;
    }

    /// <summary>
    /// The normalized endpoint (without a trailing slash).
    /// </summary>
    public Uri BaseUri => _baseUri;

    /// <summary>
    /// The model used when a call does not specify one, or <c>null</c>.
    /// </summary>
    public string? DefaultModel { get; }

    /// <summary>
    /// Validates and normalizes <paramref name="endpoint"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The endpoint is not an absolute HTTP(S) URI, or uses cleartext HTTP to a remote host without opt-in.</exception>
    public static GeminiEndpoint Create(Uri? endpoint, string? defaultModel, bool allowInsecureHttp)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("GeminiClientOptions.Endpoint must be an absolute HTTP or HTTPS URI.", nameof(endpoint));
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp && !endpoint.IsLoopback)
        {
            throw new ArgumentException(
                "Plain 'http://' endpoints are only permitted for loopback hosts (localhost / 127.0.0.1 / ::1) by default. " +
                "Set GeminiClientOptions.AllowInsecureHttp = true if you intentionally want to send credentials over unencrypted HTTP.",
                nameof(endpoint));
        }

        if (endpoint.AbsolutePath.Contains(':'))
        {
            throw new ArgumentException(
                "GeminiClientOptions.Endpoint must not contain an operation such as ':generateContent'; the client appends it automatically.",
                nameof(endpoint));
        }

        var normalized = new UriBuilder(endpoint) { Path = endpoint.AbsolutePath.TrimEnd('/') }.Uri;
        return new GeminiEndpoint(normalized, string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim());
    }

    /// <summary>
    /// Builds the URI for <paramref name="operation"/> on <paramref name="model"/> (or <see cref="DefaultModel"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither <paramref name="model"/> nor <see cref="DefaultModel"/> is set.</exception>
    public Uri GetOperationUri(string? model, string operation, bool serverSentEvents)
    {
        var resourcePath = ToResourcePath(string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim())
            ?? throw new InvalidOperationException(
                "No model was specified. Set GeminiClientOptions.Model or pass a model to the call (ChatOptions.ModelId for IChatClient).");

        var builder = new UriBuilder(_baseUri)
        {
            Path = $"{_baseUri.AbsolutePath.TrimEnd('/')}/{resourcePath}:{operation}"
        };

        if (serverSentEvents)
        {
            var existingQuery = builder.Query.TrimStart('?');
            builder.Query = existingQuery.Length == 0 ? "alt=sse" : $"{existingQuery}&alt=sse";
        }

        return builder.Uri;
    }

    /// <summary>
    /// Converts a model identifier to a URL path relative to the endpoint. Bare IDs (<c>gemini-2.5-flash</c>) become
    /// <c>models/gemini-2.5-flash</c>; resource names containing <c>/</c> (<c>models/x</c>, <c>tunedModels/x</c>) are used as-is.
    /// Each segment is percent-encoded.
    /// </summary>
    private static string? ToResourcePath(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return null;
        }

        var path = model.Contains('/') ? model.Trim('/') : ModelsSegment + model;
        return string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    }
}

