using System.Text;

namespace GeminiAPIWrapper.Http;

/// <summary>
/// Immutable, validated representation of the configured endpoint that composes operation URIs such as
/// <c>{endpoint}/models/{model}:generateContent</c> while preserving any query string on the endpoint.
/// </summary>
internal sealed class GeminiEndpoint
{
    private const string ModelsSegment = "models/";

    private readonly Uri _baseUri;
    private readonly Uri? _defaultGenerateContentUri;
    private readonly Uri? _defaultStreamSseUri;
    private readonly Uri? _defaultStreamJsonUri;

    private GeminiEndpoint(Uri baseUri, string? defaultModel)
    {
        _baseUri = baseUri;
        DefaultModel = defaultModel;

        if (ToResourcePath(defaultModel) is { } defaultResourcePath)
        {
            _defaultGenerateContentUri = BuildOperationUri(_baseUri, defaultResourcePath, "generateContent", serverSentEvents: false);
            _defaultStreamSseUri = BuildOperationUri(_baseUri, defaultResourcePath, "streamGenerateContent", serverSentEvents: true);
            _defaultStreamJsonUri = BuildOperationUri(_baseUri, defaultResourcePath, "streamGenerateContent", serverSentEvents: false);
        }
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
        if (string.IsNullOrWhiteSpace(model))
        {
            if (_defaultGenerateContentUri is not null)
            {
                if (!serverSentEvents && string.Equals(operation, "generateContent", StringComparison.Ordinal))
                {
                    return _defaultGenerateContentUri;
                }

                if (string.Equals(operation, "streamGenerateContent", StringComparison.Ordinal))
                {
                    return serverSentEvents ? _defaultStreamSseUri! : _defaultStreamJsonUri!;
                }
            }
        }

        var resourcePath = ToResourcePath(string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim())
            ?? throw new InvalidOperationException(
                "No model was specified. Set GeminiClientOptions.Model or pass a model to the call (ChatOptions.ModelId for IChatClient).");

        return BuildOperationUri(_baseUri, resourcePath, operation, serverSentEvents);
    }

    private static Uri BuildOperationUri(Uri baseUri, string resourcePath, string operation, bool serverSentEvents)
    {
        var builder = new UriBuilder(baseUri)
        {
            Path = $"{baseUri.AbsolutePath.TrimEnd('/')}/{resourcePath}:{operation}"
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
    /// Each segment is percent-encoded without intermediate array allocations.
    /// </summary>
    private static string? ToResourcePath(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return null;
        }

        var span = model.AsSpan().Trim('/');
        if (span.IsEmpty)
        {
            return null;
        }

        if (!span.Contains('/'))
        {
            return string.Concat(ModelsSegment, Uri.EscapeDataString(span));
        }

        var sb = new StringBuilder(span.Length + 8);
        var first = true;
        foreach (var range in span.Split('/'))
        {
            var segment = span[range];
            if (segment.IsEmpty || segment.IsWhiteSpace())
            {
                throw new ArgumentException($"Model resource name '{model}' contains an empty path segment.", nameof(model));
            }

            if (!first)
            {
                sb.Append('/');
            }

            first = false;
            sb.Append(Uri.EscapeDataString(segment));
        }

        return sb.ToString();
    }
}

