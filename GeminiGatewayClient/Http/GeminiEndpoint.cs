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

        if (HasOperationSuffix(endpoint.AbsolutePath))
        {
            throw new ArgumentException(
                "GeminiClientOptions.Endpoint must not contain an operation such as ':generateContent'; the client appends it automatically.",
                nameof(endpoint));
        }

        var normalized = endpoint.AbsolutePath.EndsWith('/') && endpoint.AbsolutePath.Length > 1
            ? new Uri($"{endpoint.GetLeftPart(UriPartial.Authority)}{endpoint.AbsolutePath.TrimEnd('/')}{endpoint.Query}", UriKind.Absolute)
            : endpoint;
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

        var resourcePath = ToResourcePath(string.IsNullOrWhiteSpace(model) ? DefaultModel : model)
            ?? throw new InvalidOperationException(
                "No model was specified. Set GeminiClientOptions.Model or pass a model to the call (ChatOptions.ModelId for IChatClient).");

        return BuildOperationUri(_baseUri, resourcePath, operation, serverSentEvents);
    }

    private static bool HasOperationSuffix(string absolutePath)
    {
        var span = absolutePath.AsSpan().TrimEnd('/');
        var lastSlash = span.LastIndexOf('/');
        var lastSegment = lastSlash >= 0 ? span[(lastSlash + 1)..] : span;
        return lastSegment.Contains(':');
    }

    private static Uri BuildOperationUri(Uri baseUri, string resourcePath, string operation, bool serverSentEvents)
    {
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var query = baseUri.Query.AsSpan().TrimStart('?');
        string fullQuery;
        if (serverSentEvents)
        {
            if (query.IsEmpty)
            {
                fullQuery = "?alt=sse";
            }
            else if (ContainsAltSseParameter(query))
            {
                fullQuery = string.Concat("?", query);
            }
            else
            {
                fullQuery = string.Concat("?", query, "&alt=sse");
            }
        }
        else
        {
            fullQuery = query.IsEmpty ? string.Empty : string.Concat("?", query);
        }

        return new Uri($"{baseUri.GetLeftPart(UriPartial.Authority)}{basePath}/{resourcePath}:{operation}{fullQuery}", UriKind.Absolute);
    }

    private static bool ContainsAltSseParameter(ReadOnlySpan<char> query)
    {
        foreach (var range in query.Split('&'))
        {
            if (query[range].Equals("alt=sse", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

        var span = model.AsSpan().Trim().Trim('/');
        if (span.IsEmpty)
        {
            return null;
        }

        if (span.Contains(':'))
        {
            throw new ArgumentException(
                $"Model name '{model}' must not contain an operation such as ':generateContent'; the client appends it automatically.",
                nameof(model));
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

