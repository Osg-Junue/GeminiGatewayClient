using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GeminiAPIWrapper.ApiException;
using GeminiAPIWrapper.Options;

namespace GeminiAPIWrapper;

/// <summary>
/// HTTP client implementation for the Gemini (Generative Language) API.
/// <para>
/// Headers are applied per <see cref="HttpRequestMessage"/> rather than on <see cref="HttpClient.DefaultRequestHeaders"/>,
/// so a shared <see cref="HttpClient"/> (for example from <c>IHttpClientFactory</c>) is never mutated.
/// </para>
/// </summary>
internal sealed class GeminiClient : IGeminiClient, IDisposable
{
    private const string GenerateContentOperation = "generateContent";
    private const string StreamGenerateContentOperation = "streamGenerateContent";
    private const int MaxErrorBodyChars = 64 * 1024;

    private static readonly TimeSpan s_pooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUri;
    private readonly string _contentType;
    private readonly GeminiStreamingFormat _streamingFormat;
    private readonly KeyValuePair<string, string>[] _headers;

    /// <summary>
    /// Initializes a new <see cref="GeminiClient"/> with the specified <paramref name="options"/>
    /// and an optional externally managed <paramref name="httpClient"/>.
    /// </summary>
    public GeminiClient(GenerativeLanguageOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EndpointBase, nameof(options.EndpointBase));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyHeader, nameof(options.KeyHeader));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContentType, nameof(options.ContentType));

        if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        _baseUri = ParseAndValidateBaseUri(options.EndpointBase, options.AllowInsecureHttp);
        _contentType = options.ContentType;
        _streamingFormat = options.StreamingFormat;
        _headers = BuildAndValidateHeaders(options);

        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? CreateDefaultHttpClient(options.Timeout ?? GenerativeLanguageOptions.DefaultTimeout);
    }

    internal TimeSpan HttpClientTimeout => _httpClient.Timeout;

    /// <inheritdoc />
    public async Task<GeminiResponse?> GenerateAsync(
        GeminiRequest request,
        CancellationToken cancellationToken = default,
        string? modelOverride = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        using var httpRequest = BuildRequestMessage(GenerateContentOperation, request, useSse: false, modelOverride);
        using var responseMessage = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!responseMessage.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(responseMessage, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await responseMessage.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonSerializer
                .DeserializeAsync(stream, GeminiResponseJsonContext.Default.GeminiResponse, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new GeminiApiException("Failed to deserialize the Gemini API response.", responseMessage.StatusCode, null, ex);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GeminiResponse?> StreamGenerateAsync(
        GeminiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string? modelOverride = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var useSse = _streamingFormat == GeminiStreamingFormat.ServerSentEvents;
        using var httpRequest = BuildRequestMessage(StreamGenerateContentOperation, request, useSse, modelOverride);
        using var responseMessage = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!responseMessage.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(responseMessage, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await responseMessage.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        if (useSse)
        {
            await foreach (var chunk in ReadSseStreamAsync(stream, responseMessage.StatusCode, cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        else
        {
            await foreach (var chunk in ReadJsonArrayStreamAsync(stream, responseMessage.StatusCode, cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// Disposes the underlying <see cref="HttpClient"/> if it was created by this instance.
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateDefaultHttpClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = s_pooledConnectionLifetime
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout
        };
    }

    private static Uri ParseAndValidateBaseUri(string endpointBase, bool allowInsecureHttp)
    {
        if (!Uri.TryCreate(endpointBase.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("EndpointBase must be an absolute HTTP or HTTPS URL.", nameof(endpointBase));
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp && !parsed.IsLoopback)
        {
            throw new ArgumentException(
                "Plain 'http://' endpoints are only permitted for loopback hosts (localhost / 127.0.0.1 / ::1) by default. " +
                "Set GenerativeLanguageOptions.AllowInsecureHttp = true if you intentionally want to send credentials over unencrypted HTTP.",
                nameof(endpointBase));
        }

        var builder = new UriBuilder(parsed)
        {
            Path = parsed.AbsolutePath.TrimEnd('/', ':')
        };
        return builder.Uri;
    }

    private static KeyValuePair<string, string>[] BuildAndValidateHeaders(GenerativeLanguageOptions options)
    {
        var headers = new List<KeyValuePair<string, string>>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var probeMessage = new HttpRequestMessage();

        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            var keyValue = $"{options.KeyPrefix}{options.ApiKey}";
            ValidateSingleHeader(probeMessage, options.KeyHeader, keyValue, nameof(options.KeyHeader));
            seenNames.Add(options.KeyHeader);
            headers.Add(new KeyValuePair<string, string>(options.KeyHeader, keyValue));
        }

        if (options.AdditionalHeaders is not null)
        {
            foreach (var (name, rawValue) in options.AdditionalHeaders)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(options.AdditionalHeaders));
                var value = rawValue ?? string.Empty;

                if (!seenNames.Add(name))
                {
                    throw new ArgumentException(
                        $"Duplicate header '{name}' in AdditionalHeaders (conflicts with KeyHeader or another additional header).",
                        nameof(options.AdditionalHeaders));
                }

                ValidateSingleHeader(probeMessage, name, value, nameof(options.AdditionalHeaders));
                headers.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return [.. headers];
    }

    private static void ValidateSingleHeader(HttpRequestMessage probeMessage, string name, string value, string paramName)
    {
        if (name.AsSpan().IndexOfAny('\r', '\n') >= 0 || value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException($"Header '{name}' contains invalid newline characters.", paramName);
        }

        if (!probeMessage.Headers.TryAddWithoutValidation(name, value))
        {
            throw new ArgumentException(
                $"Header '{name}' is not a valid request header (content headers such as 'Content-Type' cannot be set via AdditionalHeaders or KeyHeader).",
                paramName);
        }
    }

    private static void ValidateRequest(GeminiRequest request)
    {
        var genConfig = request.GenerationConfig;
        if (genConfig is not null)
        {
            if (genConfig.ThinkingConfig is { ThinkingBudget: not null, ThinkingLevel: not null })
            {
                throw new ArgumentException(
                    "ThinkingConfig.ThinkingBudget and ThinkingConfig.ThinkingLevel are mutually exclusive and cannot both be set.",
                    nameof(request));
            }

            if (genConfig.ResponseSchema is not null && genConfig.ResponseJsonSchema is not null)
            {
                throw new ArgumentException(
                    "GenerationConfig.ResponseSchema and GenerationConfig.ResponseJsonSchema are mutually exclusive and cannot both be set.",
                    nameof(request));
            }
        }

        if (request.Tools is not null)
        {
            foreach (var tool in request.Tools)
            {
                if (tool?.FunctionDeclarations is null)
                {
                    continue;
                }

                foreach (var fn in tool.FunctionDeclarations)
                {
                    if (fn.Parameters is not null && fn.ParametersJsonSchema is not null)
                    {
                        throw new ArgumentException(
                            $"FunctionDeclaration '{fn.Name}' cannot set both Parameters and ParametersJsonSchema.",
                            nameof(request));
                    }
                }
            }
        }
    }

    private HttpRequestMessage BuildRequestMessage(
        string operation,
        GeminiRequest request,
        bool useSse,
        string? modelOverride)
    {
        var requestUri = BuildOperationUri(operation, useSse, modelOverride);
        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(request, GeminiRequestJsonContext.Default.GeminiRequest);
        var content = new ByteArrayContent(utf8Json);
        content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);

        var message = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = content
        };

        if (useSse)
        {
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        foreach (var (name, value) in _headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        return message;
    }

    private Uri BuildOperationUri(string operation, bool useSse, string? modelOverride)
    {
        var path = _baseUri.AbsolutePath;
        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            path = ReplaceModelInPath(path, modelOverride);
        }

        var builder = new UriBuilder(_baseUri)
        {
            Path = $"{path}:{operation}"
        };

        if (useSse)
        {
            var existingQuery = builder.Query.TrimStart('?');
            builder.Query = string.IsNullOrEmpty(existingQuery)
                ? "alt=sse"
                : $"{existingQuery}&alt=sse";
        }

        return builder.Uri;
    }

    private static string ReplaceModelInPath(string path, string modelOverride)
    {
        const string modelsSegment = "/models/";
        var idx = path.LastIndexOf(modelsSegment, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            throw new InvalidOperationException(
                $"Cannot apply ChatOptions.ModelId ('{modelOverride}') because EndpointBase does not contain a '/models/{{model}}' path segment.");
        }

        var normalizedModel = modelOverride.Trim();
        if (normalizedModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedModel = normalizedModel["models/".Length..];
        }

        var afterModels = idx + modelsSegment.Length;
        var nextSlash = path.IndexOf('/', afterModels);
        var encodedModel = Uri.EscapeDataString(normalizedModel);

        return nextSlash < 0
            ? string.Concat(path.AsSpan(0, afterModels), encodedModel)
            : string.Concat(path.AsSpan(0, afterModels), encodedModel, path.AsSpan(nextSlash));
    }

    private static async IAsyncEnumerable<GeminiResponse?> ReadSseStreamAsync(
        Stream stream,
        HttpStatusCode statusCode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var dataBuffer = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                if (dataBuffer.Length > 0)
                {
                    yield return DeserializeSsePayload(dataBuffer.ToString(), statusCode);
                    dataBuffer.Clear();
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (dataBuffer.Length > 0)
                {
                    yield return DeserializeSsePayload(dataBuffer.ToString(), statusCode);
                    dataBuffer.Clear();
                }

                continue;
            }

            if (line[0] == ':')
            {
                // SSE comment / keep-alive line
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line.AsSpan(5);
                if (payload.Length > 0 && payload[0] == ' ')
                {
                    payload = payload[1..];
                }

                if (payload.SequenceEqual("[DONE]".AsSpan()))
                {
                    continue;
                }

                if (dataBuffer.Length > 0)
                {
                    dataBuffer.Append('\n');
                }

                dataBuffer.Append(payload);
            }
        }
    }

    private static GeminiResponse? DeserializeSsePayload(string json, HttpStatusCode statusCode)
    {
        try
        {
            return JsonSerializer.Deserialize(json, GeminiResponseJsonContext.Default.GeminiResponse);
        }
        catch (JsonException ex)
        {
            throw new GeminiApiException("Failed to deserialize a Gemini SSE streaming chunk.", statusCode, null, ex);
        }
    }

    private static async IAsyncEnumerable<GeminiResponse?> ReadJsonArrayStreamAsync(
        Stream stream,
        HttpStatusCode statusCode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = JsonSerializer
            .DeserializeAsyncEnumerable(stream, GeminiResponseJsonContext.Default.GeminiResponse, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            GeminiResponse? current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch (JsonException ex)
            {
                throw new GeminiApiException("Failed to deserialize a Gemini streaming chunk.", statusCode, null, ex);
            }

            yield return current;
        }
    }

    private static async Task<GeminiApiException> CreateApiExceptionAsync(
        HttpResponseMessage responseMessage,
        CancellationToken cancellationToken)
    {
        var errorContent = await ReadBoundedErrorBodyAsync(responseMessage.Content, cancellationToken).ConfigureAwait(false);
        ParseGoogleErrorPayload(errorContent, out var errorStatus, out var errorMessage, out var retryDelay);

        if (retryDelay is null && responseMessage.Headers.RetryAfter is { } retryAfterHeader)
        {
            if (retryAfterHeader.Delta is { } delta)
            {
                retryDelay = delta;
            }
            else if (retryAfterHeader.Date is { } date)
            {
                var diff = date - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    retryDelay = diff;
                }
            }
        }

        return new GeminiApiException(
            "API request failed",
            responseMessage.StatusCode,
            errorContent,
            errorStatus,
            errorMessage,
            retryDelay);
    }

    private static async Task<string> ReadBoundedErrorBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);

        var buffer = new char[4096];
        var sb = new StringBuilder();
        var remaining = MaxErrorBodyChars;

        while (remaining > 0)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            sb.Append(buffer, 0, read);
            remaining -= read;
        }

        return sb.ToString();
    }

    private static void ParseGoogleErrorPayload(
        string? errorBody,
        out string? errorStatus,
        out string? errorMessage,
        out TimeSpan? retryDelay)
    {
        errorStatus = null;
        errorMessage = null;
        retryDelay = null;

        if (string.IsNullOrWhiteSpace(errorBody))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var errorObj) || errorObj.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (errorObj.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
            {
                errorStatus = statusProp.GetString();
            }

            if (errorObj.TryGetProperty("message", out var messageProp) && messageProp.ValueKind == JsonValueKind.String)
            {
                errorMessage = messageProp.GetString();
            }

            if (errorObj.TryGetProperty("details", out var detailsProp) && detailsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in detailsProp.EnumerateArray())
                {
                    if (detail.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (detail.TryGetProperty("retryDelay", out var delayProp) &&
                        delayProp.ValueKind == JsonValueKind.String &&
                        TryParseProtobufDuration(delayProp.GetString(), out var parsedDelay))
                    {
                        retryDelay = parsedDelay;
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON or truncated error body; keep raw ErrorContent only.
        }
    }

    private static bool TryParseProtobufDuration(string? value, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        if (span.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(span[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms >= 0)
            {
                duration = TimeSpan.FromMilliseconds(ms);
                return true;
            }

            return false;
        }

        if (span.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(span[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            {
                duration = TimeSpan.FromSeconds(seconds);
                return true;
            }
        }

        return false;
    }
}
