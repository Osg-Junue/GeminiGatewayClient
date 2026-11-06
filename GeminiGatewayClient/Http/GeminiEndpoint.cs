using System.Text;

namespace GeminiGatewayClient.Http;

/// <summary>
/// Immutable, validated representation of the configured endpoint that composes operation URIs such as
/// <c>{endpoint}/models/{model}:generateContent</c> while preserving any query string on the endpoint.
/// </summary>
internal sealed class GeminiEndpoint
{
    private const string ModelsSegment = "models/";

    private readonly Uri _baseUri;

    private GeminiEndpoint(Uri baseUri, string? defaultModel, string? defaultEmbeddingModel)
    {
        _baseUri = baseUri;
        DefaultModel = defaultModel;
        DefaultEmbeddingModel = defaultEmbeddingModel ?? defaultModel;

        // Reports an invalid configured model at startup rather than on the first call.
        _ = ToResourcePath(DefaultModel);
        _ = ToResourcePath(DefaultEmbeddingModel);
    }

    /// <summary>
    /// The normalized endpoint (without a trailing slash).
    /// </summary>
    public Uri BaseUri => _baseUri;

    /// <summary>
    /// The model used when a content-generation call does not specify one, or <c>null</c>.
    /// </summary>
    public string? DefaultModel { get; }

    /// <summary>
    /// The model used when an embedding call does not specify one, or <c>null</c>.
    /// </summary>
    public string? DefaultEmbeddingModel { get; }

    /// <summary>
    /// Validates and normalizes <paramref name="endpoint"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The endpoint is not an absolute HTTP(S) URI, or uses cleartext HTTP to a remote host without opt-in.</exception>
    public static GeminiEndpoint Create(
        Uri? endpoint,
        string? defaultModel,
        bool allowInsecureHttp,
        string? defaultEmbeddingModel = null)
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

        if (HasOperationSuffix(endpoint.AbsolutePath))
        {
            throw new ArgumentException(
                "GeminiClientOptions.Endpoint must not contain an operation such as ':generateContent'; the client appends it automatically.",
                nameof(endpoint));
        }

        var normalized = endpoint.AbsolutePath.EndsWith('/') && endpoint.AbsolutePath.Length > 1
            ? new Uri($"{endpoint.GetLeftPart(UriPartial.Authority)}{endpoint.AbsolutePath.TrimEnd('/')}{endpoint.Query}", UriKind.Absolute)
            : endpoint;
        return new GeminiEndpoint(
            normalized,
            string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim(),
            string.IsNullOrWhiteSpace(defaultEmbeddingModel) ? null : defaultEmbeddingModel.Trim());
    }

    /// <summary>
    /// Builds the URI for <paramref name="operation"/> on <paramref name="model"/> (or <see cref="DefaultModel"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither <paramref name="model"/> nor <see cref="DefaultModel"/> is set.</exception>
    public Uri GetOperationUri(string? model, string operation, bool serverSentEvents)
    {
        var resourcePath = ToResourcePath(string.IsNullOrWhiteSpace(model) ? DefaultModel : model)
            ?? throw new InvalidOperationException(
                "No model was specified. Set GeminiClientOptions.Model or pass a model to the call (ChatOptions.ModelId for IChatClient).");

        return BuildOperationUri(_baseUri, resourcePath, operation, serverSentEvents);
    }

    /// <summary>
    /// Builds the URI for an embedding <paramref name="operation"/> on <paramref name="model"/> (or <see cref="DefaultEmbeddingModel"/>)
    /// and outputs the canonical <paramref name="resourcePath"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither <paramref name="model"/> nor <see cref="DefaultEmbeddingModel"/> is set.</exception>
    public Uri GetEmbeddingOperationUri(string? model, string operation, out string resourcePath)
    {
        resourcePath = ToResourcePath(string.IsNullOrWhiteSpace(model) ? DefaultEmbeddingModel : model)
            ?? throw new InvalidOperationException(
                "No embedding model was specified. Set GeminiClientOptions.EmbeddingModel (or GeminiClientOptions.Model) or pass a model to the call (EmbeddingGenerationOptions.ModelId for IEmbeddingGenerator).");

        return BuildOperationUri(_baseUri, resourcePath, operation, serverSentEvents: false);
    }

    private static bool HasOperationSuffix(string absolutePath) =>
        absolutePath.TrimEnd('/').Split('/')[^1].Contains(':');

    private static Uri BuildOperationUri(Uri baseUri, string resourcePath, string operation, bool serverSentEvents)
    {
        var query = baseUri.Query.TrimStart('?');
        if (serverSentEvents && !query.Split('&').Contains("alt=sse", StringComparer.OrdinalIgnoreCase))
        {
            query = query.Length == 0 ? "alt=sse" : $"{query}&alt=sse";
        }

        var address = $"{baseUri.GetLeftPart(UriPartial.Authority)}{baseUri.AbsolutePath.TrimEnd('/')}/{resourcePath}:{operation}";
        return new Uri(query.Length == 0 ? address : $"{address}?{query}", UriKind.Absolute);
    }

    /// <summary>
    /// Converts a model identifier to a URL path relative to the endpoint. Bare IDs (<c>gemini-2.5-flash</c>) become
    /// <c>models/gemini-2.5-flash</c>; resource names containing <c>/</c> (<c>models/x</c>, <c>tunedModels/x</c>) are used as-is.
    /// Each segment is percent-encoded.
    /// </summary>
    internal static string? ToResourcePath(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var segments = model.Trim().Trim('/').Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new ArgumentException($"Model resource name '{model}' contains an empty or relative path segment.", nameof(model));
        }

        if (model.Contains(':'))
        {
            throw new ArgumentException(
                $"Model name '{model}' must not contain an operation such as ':generateContent'; the client appends it automatically.",
                nameof(model));
        }

        var path = string.Join('/', segments.Select(Uri.EscapeDataString));
        return segments.Length == 1 ? ModelsSegment + path : path;
    }
}
