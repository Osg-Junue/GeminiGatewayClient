using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GeminiGatewayClient.Http;
using GeminiGatewayClient.Models;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient;

/// <summary>
/// HTTP client for the Gemini (Generative Language) API that works directly against Google or behind API gateways and proxies.
/// <para>
/// Headers are applied per <see cref="HttpRequestMessage"/> rather than on <see cref="HttpClient.DefaultRequestHeaders"/>,
/// so a shared <see cref="HttpClient"/> (for example from <c>IHttpClientFactory</c>) is never mutated.
/// Use <see cref="GeminiClientExtensions.AsIChatClient(IGeminiClient, string?, JsonSerializerOptions?)"/> to obtain a
/// <c>Microsoft.Extensions.AI</c> <c>IChatClient</c>, or
/// <see cref="GeminiClientExtensions.AsIEmbeddingGenerator(IGeminiClient, string?, int?)"/> to obtain an
/// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>.
/// </para>
/// </summary>
public sealed class GeminiClient : IGeminiClient, IDisposable
{
    private const string GenerateContentOperation = "generateContent";
    private const string StreamGenerateContentOperation = "streamGenerateContent";
    private const string EmbedContentOperation = "embedContent";
    private const string BatchEmbedContentsOperation = "batchEmbedContents";
    private const int MaxPreallocatedBufferBytes = 10 * 1024 * 1024;
    private const int ReadChunkBytes = 4096;
    internal const int MaxBufferedResponseBytes = 64 * 1024 * 1024;

    private static readonly TimeSpan s_pooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private readonly GeminiClientSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="GeminiClient"/> for the Gemini Developer API using the default endpoint and header.
    /// </summary>
    /// <param name="apiKey">The Gemini API key.</param>
    /// <param name="model">The default model, for example <c>gemini-2.5-flash</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> or <paramref name="model"/> is <c>null</c> or whitespace.</exception>
    public GeminiClient(string apiKey, string model)
        : this(CreateDefaultOptions(apiKey, model))
    {
    }

    /// <summary>
    /// Initializes a new <see cref="GeminiClient"/> with the specified <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Endpoint, model, credential, and transport settings. A validated snapshot is taken.</param>
    /// <param name="httpClient">
    /// Optional caller-managed <see cref="HttpClient"/> (for example from <c>IHttpClientFactory</c> or a custom handler pipeline).
    /// It is not disposed by this instance and <see cref="GeminiClientOptions.Timeout"/> is not applied to it.
    /// When <c>null</c>, an internal client that does not follow redirects is created and owned by this instance.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> is invalid.</exception>
    public GeminiClient(GeminiClientOptions options, HttpClient? httpClient = null)
        : this(GeminiClientSettings.Create(options), httpClient, disposeHttpClient: httpClient is null)
    {
    }

    internal GeminiClient(GeminiClientSettings settings, HttpClient? httpClient, bool disposeHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _disposeHttpClient = disposeHttpClient;
        _httpClient = httpClient ?? CreateDefaultHttpClient(_settings.Timeout);
    }

    /// <inheritdoc />
    public Uri Endpoint => _settings.Endpoint.BaseUri;

    /// <inheritdoc />
    public string? DefaultModel => _settings.Endpoint.DefaultModel;

    /// <inheritdoc />
    public string? DefaultEmbeddingModel => _settings.Endpoint.DefaultEmbeddingModel;

    internal TimeSpan HttpClientTimeout => _httpClient.Timeout;

    internal JsonSerializerOptions? ToolSerializerOptions => _settings.ToolSerializerOptions;

    /// <inheritdoc />
    public Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var requestUri = PrepareRequest(request, model, GenerateContentOperation, serverSentEvents: false);
        return SendBufferedAsync(
            requestUri,
            request,
            GeminiJsonContext.Wire.GenerateContentRequest,
            GeminiJsonContext.Default.GenerateContentResponse,
            static r => r.ExtensionData,
            cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<GenerateContentResponse> StreamGenerateContentAsync(
        GenerateContentRequest request,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        // Validate eagerly: errors in an `async` iterator would only surface on the first MoveNextAsync.
        var serverSentEvents = _settings.StreamingFormat == GeminiStreamingFormat.ServerSentEvents;
        var requestUri = PrepareRequest(request, model, StreamGenerateContentOperation, serverSentEvents);
        return StreamCoreAsync(requestUri, request, serverSentEvents, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(nameof(request));

        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : request.Model;
        var requestUri = _settings.Endpoint.GetEmbeddingOperationUri(effectiveModel, EmbedContentOperation, out var targetResourcePath);
        var wireRequest = NormalizeSingleEmbedRequestModel(request, targetResourcePath, effectiveModel);
        return SendBufferedAsync(
            requestUri,
            wireRequest,
            GeminiJsonContext.Wire.EmbedContentRequest,
            GeminiJsonContext.Default.EmbedContentResponse,
            static r => r.ExtensionData,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<BatchEmbedContentsResponse> BatchEmbedContentsAsync(
        BatchEmbedContentsRequest request,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(nameof(request));

        var effectiveModel = ResolveEffectiveBatchModel(request, model);
        var requestUri = _settings.Endpoint.GetEmbeddingOperationUri(effectiveModel, BatchEmbedContentsOperation, out var targetResourcePath);
        var wireRequest = NormalizeBatchRequestModels(request, targetResourcePath, effectiveModel);

        return SendBufferedAsync(
            requestUri,
            wireRequest,
            GeminiJsonContext.Wire.BatchEmbedContentsRequest,
            GeminiJsonContext.Default.BatchEmbedContentsResponse,
            static r => r.ExtensionData,
            cancellationToken);
    }

    /// <summary>
    /// Disposes the internal <see cref="HttpClient"/> when it was created by this instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<TResponse> SendBufferedAsync<TRequest, TResponse>(
        Uri requestUri,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        Func<TResponse, IDictionary<string, JsonElement>?> extensionDataSelector,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var httpRequest = await CreateRequestMessageAsync(requestUri, request, requestTypeInfo, serverSentEvents: false, cancellationToken).ConfigureAwait(false);
        using var httpResponse = await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        using var watchdog = new StreamIdleWatchdog(_settings.StreamIdleTimeout, cancellationToken);

        try
        {
            var contentLength = httpResponse.Content.Headers.ContentLength;
            if (contentLength > MaxBufferedResponseBytes)
            {
                throw new GeminiApiException("The Gemini API response body exceeded the maximum allowed length.", httpResponse.StatusCode);
            }

            var stream = await httpResponse.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var initialCapacity = contentLength is > 0 and <= MaxPreallocatedBufferBytes and var length
                    ? (int)length + ReadChunkBytes
                    : ReadChunkBytes;
                using var buffer = new PooledByteBufferWriter(initialCapacity);
                while (true)
                {
                    int read;
                    watchdog.BeforeRead();
                    var readSucceeded = false;
                    try
                    {
                        read = await stream.ReadAsync(buffer.GetMemory(ReadChunkBytes), watchdog.Token).ConfigureAwait(false);
                        readSucceeded = true;
                    }
                    finally
                    {
                        watchdog.AfterRead(readSucceeded);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    if (read > MaxBufferedResponseBytes - buffer.WrittenCount)
                    {
                        throw new GeminiApiException("The Gemini API response body exceeded the maximum allowed length.", httpResponse.StatusCode);
                    }

                    buffer.Advance(read);
                }

                return GeminiStreamReader.DeserializeUtf8Response(
                    buffer.WrittenSpan,
                    responseTypeInfo,
                    extensionDataSelector,
                    httpResponse.StatusCode,
                    "The Gemini API returned an empty (null) response body.",
                    "Failed to deserialize the Gemini API response.",
                    "API request failed");
            }
        }
        catch (OperationCanceledException ex) when (watchdog.HasTimedOut)
        {
            throw new TimeoutException(
                $"No data was received from the Gemini response within {watchdog.IdleTimeout} (GeminiClientOptions.StreamIdleTimeout).",
                ex);
        }
    }

    private async IAsyncEnumerable<GenerateContentResponse> StreamCoreAsync(
        Uri requestUri,
        GenerateContentRequest request,
        bool serverSentEvents,
        CancellationToken requestCancellationToken,
        [EnumeratorCancellation] CancellationToken enumeratorCancellationToken = default)
    {
        using var linkedCts = CreateLinkedTokenSource(requestCancellationToken, enumeratorCancellationToken, out var cancellationToken);
        using var httpRequest = await CreateRequestMessageAsync(
            requestUri,
            request,
            GeminiJsonContext.Wire.GenerateContentRequest,
            serverSentEvents,
            cancellationToken).ConfigureAwait(false);
        using var httpResponse = await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var watchdog = new StreamIdleWatchdog(_settings.StreamIdleTimeout, cancellationToken);

            var chunks = GeminiStreamReader
                .ReadAsync(stream, _settings.StreamingFormat, httpResponse.StatusCode, watchdog)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    GenerateContentResponse chunk;
                    try
                    {
                        if (!await chunks.MoveNextAsync().ConfigureAwait(false))
                        {
                            yield break;
                        }

                        chunk = chunks.Current;
                    }
                    catch (OperationCanceledException ex) when (watchdog.HasTimedOut)
                    {
                        throw new TimeoutException(
                            $"No data was received from the Gemini streaming response within {watchdog.IdleTimeout} (GeminiClientOptions.StreamIdleTimeout).",
                            ex);
                    }

                    yield return chunk;
                }
            }
            finally
            {
                await chunks.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static CancellationTokenSource? CreateLinkedTokenSource(
        CancellationToken token1,
        CancellationToken token2,
        out CancellationToken effectiveToken)
    {
        if (token1.CanBeCanceled && token2.CanBeCanceled && token1 != token2)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(token1, token2);
            effectiveToken = cts.Token;
            return cts;
        }

        effectiveToken = token1.CanBeCanceled ? token1 : token2;
        return null;
    }

    private Uri PrepareRequest(GenerateContentRequest request, string? model, string operation, bool serverSentEvents)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(nameof(request));
        return _settings.Endpoint.GetOperationUri(model, operation, serverSentEvents);
    }

    private static EmbedContentRequest NormalizeSingleEmbedRequestModel(
        EmbedContentRequest request,
        string targetResourcePath,
        string? validatedModel)
    {
        if (string.Equals(request.Model, targetResourcePath, StringComparison.Ordinal))
        {
            return request;
        }

        if (!string.IsNullOrWhiteSpace(request.Model) &&
            !string.Equals(request.Model, validatedModel, StringComparison.Ordinal))
        {
            // Validate request.Model even when overridden by the per-call model argument.
            _ = GeminiEndpoint.ToResourcePath(request.Model);
        }

        return request.Clone(modelOverride: targetResourcePath);
    }

    private static string? ResolveEffectiveBatchModel(BatchEmbedContentsRequest request, string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        var items = request.Requests;
        for (var i = 0; i < items.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(items[i].Model))
            {
                return items[i].Model;
            }
        }

        return null;
    }

    private static BatchEmbedContentsRequest NormalizeBatchRequestModels(
        BatchEmbedContentsRequest request,
        string targetResourcePath,
        string? validatedModel)
    {
        var items = request.Requests;
        var needsCopy = false;
        var lastValidatedRawModel = validatedModel;
        for (var i = 0; i < items.Count; i++)
        {
            var itemModel = items[i].Model;
            if (string.Equals(itemModel, targetResourcePath, StringComparison.Ordinal))
            {
                continue;
            }

            needsCopy = true;
            if (!string.IsNullOrWhiteSpace(itemModel) &&
                !string.Equals(itemModel, lastValidatedRawModel, StringComparison.Ordinal))
            {
                var resolved = GeminiEndpoint.ToResourcePath(itemModel)!;
                if (!string.Equals(resolved, targetResourcePath, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"BatchEmbedContentsRequest.Requests[{i}].Model ('{itemModel}', resolved as '{resolved}') does not match the batch target model '{targetResourcePath}'. All items in a batch embedding request must target the same model.",
                        nameof(request));
                }

                lastValidatedRawModel = itemModel;
            }
        }

        if (!needsCopy)
        {
            return request;
        }

        var normalizedItems = new List<EmbedContentRequest>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            normalizedItems.Add(string.Equals(item.Model, targetResourcePath, StringComparison.Ordinal)
                ? item
                : item.Clone(modelOverride: targetResourcePath));
        }

        return new BatchEmbedContentsRequest
        {
            Requests = normalizedItems,
            ExtensionData = request.ExtensionData
        };
    }

    private ValueTask<HttpRequestMessage> CreateRequestMessageAsync<TRequest>(
        Uri requestUri,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        bool serverSentEvents,
        CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(request, requestTypeInfo);

        var message = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
        try
        {
            if (serverSentEvents)
            {
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            }

            var applyTask = _settings.Headers.ApplyAsync(message, cancellationToken);
            if (applyTask.IsCompletedSuccessfully)
            {
                applyTask.GetAwaiter().GetResult();
                return ValueTask.FromResult(message);
            }

            return CompleteRequestMessageAsync(message, applyTask);
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private static async ValueTask<HttpRequestMessage> CompleteRequestMessageAsync(
        HttpRequestMessage message,
        ValueTask applyTask)
    {
        try
        {
            await applyTask.ConfigureAwait(false);
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        using (var watchdog = new StreamIdleWatchdog(_settings.StreamIdleTimeout, cancellationToken))
        {
            try
            {
                var exception = await GeminiErrorParser.CreateFromResponseAsync(response, watchdog).ConfigureAwait(false);
                throw exception;
            }
            catch (OperationCanceledException ex) when (watchdog.HasTimedOut)
            {
                throw new TimeoutException(
                    $"No data was received while reading the Gemini error response body within {watchdog.IdleTimeout} (GeminiClientOptions.StreamIdleTimeout).",
                    ex);
            }
        }
    }

    private static HttpClient CreateDefaultHttpClient(TimeSpan timeout) =>
        new(CreateDefaultHandler(), disposeHandler: true) { Timeout = timeout };

    /// <summary>
    /// Creates the primary handler used for internally owned and factory-created clients.
    /// </summary>
    internal static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        PooledConnectionLifetime = s_pooledConnectionLifetime,
        // Credentials travel in custom headers (x-goog-api-key, Ocp-Apim-Subscription-Key, ...). .NET only strips
        // the Authorization header on cross-host redirects, so following redirects could leak those headers.
        AllowAutoRedirect = false
    };

    private static GeminiClientOptions CreateDefaultOptions(string apiKey, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return new GeminiClientOptions { ApiKey = apiKey, Model = model };
    }
}
