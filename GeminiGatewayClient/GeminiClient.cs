using System.Net.Http.Headers;
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
    internal const int MaxResponseBytes = 64 * 1024 * 1024;

    private static readonly TimeSpan s_pooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private readonly GeminiClientSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="GeminiClient"/> for the Gemini Developer API using the default endpoint and header.
    /// </summary>
    /// <param name="apiKey">The Gemini API key.</param>
    /// <param name="model">The default model, for example <c>gemini-flash-latest</c>.</param>
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
    /// Disable automatic redirects on its handler: .NET keeps custom credential headers when following a redirect to another host.
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
        _httpClient = httpClient ?? ConfigureHttpClient(new HttpClient(CreateDefaultHandler(), disposeHandler: true), settings.Timeout);
    }

    /// <inheritdoc />
    public Uri Endpoint => _settings.Endpoint.BaseUri;

    /// <inheritdoc />
    public string? DefaultModel => _settings.Endpoint.DefaultModel;

    /// <inheritdoc />
    public string? DefaultEmbeddingModel => _settings.Endpoint.DefaultEmbeddingModel;

    /// <inheritdoc />
    public JsonSerializerOptions? ToolSerializerOptions => _settings.ToolSerializerOptions;

    internal TimeSpan HttpClientTimeout => _httpClient.Timeout;

    /// <inheritdoc />
    public Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var requestUri = PrepareGenerateRequest(request, model, GenerateContentOperation, serverSentEvents: false);
        return SendAsync(
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
        var requestUri = PrepareGenerateRequest(request, model, StreamGenerateContentOperation, serverSentEvents: true);
        return StreamCoreAsync(requestUri, request, cancellationToken);
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
        var requestUri = _settings.Endpoint.GetEmbeddingOperationUri(effectiveModel, EmbedContentOperation, out var resourcePath);
        return SendAsync(
            requestUri,
            request.WithModel(resourcePath),
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

        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : request.Requests.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Model))?.Model;
        var requestUri = _settings.Endpoint.GetEmbeddingOperationUri(effectiveModel, BatchEmbedContentsOperation, out var resourcePath);
        var wireRequest = new BatchEmbedContentsRequest
        {
            Requests = [.. request.Requests.Select(item => item.WithModel(ToBatchItemModel(item, resourcePath)))],
            ExtensionData = request.ExtensionData
        };

        return SendAsync(
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

    /// <summary>
    /// Applies the settings every client needs, whether this instance owns it or a factory creates it.
    /// </summary>
    internal static HttpClient ConfigureHttpClient(HttpClient httpClient, TimeSpan timeout)
    {
        httpClient.Timeout = timeout;
        httpClient.MaxResponseContentBufferSize = MaxResponseBytes;
        return httpClient;
    }

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

    /// <summary>
    /// A batch has one model, and quietly overriding an item would return vectors from a model the caller did not ask for.
    /// </summary>
    private static string ToBatchItemModel(EmbedContentRequest item, string batchResourcePath)
    {
        var resourcePath = GeminiEndpoint.ToResourcePath(item.Model) ?? batchResourcePath;
        return resourcePath == batchResourcePath
            ? resourcePath
            : throw new ArgumentException(
                $"Every item in a batch embedding request must target the same model ('{batchResourcePath}'), but one targets '{resourcePath}'.",
                "request");
    }

    private static GeminiClientOptions CreateDefaultOptions(string apiKey, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return new GeminiClientOptions { ApiKey = apiKey, Model = model };
    }

    private Uri PrepareGenerateRequest(GenerateContentRequest request, string? model, string operation, bool serverSentEvents)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(nameof(request));
        return _settings.Endpoint.GetOperationUri(model, operation, serverSentEvents);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        Uri requestUri,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        Func<TResponse, IDictionary<string, JsonElement>?> extensionDataSelector,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var httpRequest = await CreateRequestMessageAsync(requestUri, request, requestTypeInfo, serverSentEvents: false, cancellationToken).ConfigureAwait(false);
        using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(httpResponse, cancellationToken).ConfigureAwait(false);

        var body = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return GeminiResponseReader.Parse(body, responseTypeInfo, extensionDataSelector, httpResponse.StatusCode);
    }

    private async IAsyncEnumerable<GenerateContentResponse> StreamCoreAsync(
        Uri requestUri,
        GenerateContentRequest request,
        CancellationToken requestCancellationToken,
        [EnumeratorCancellation] CancellationToken enumeratorCancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken, enumeratorCancellationToken);
        var cancellationToken = linkedCts.Token;

        using var httpRequest = await CreateRequestMessageAsync(requestUri, request, GeminiJsonContext.Wire.GenerateContentRequest, serverSentEvents: true, cancellationToken).ConfigureAwait(false);
        using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(httpResponse, cancellationToken).ConfigureAwait(false);

        var content = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = new ResponseBodyStream(content, _settings.StreamIdleTimeout, httpResponse.StatusCode);
        await using (body.ConfigureAwait(false))
        {
            await foreach (var chunk in GeminiResponseReader.ReadChunksAsync(body, httpResponse.StatusCode, _httpClient.MaxResponseContentBufferSize, cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    private async Task<HttpRequestMessage> CreateRequestMessageAsync<TRequest>(
        Uri requestUri,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        bool serverSentEvents,
        CancellationToken cancellationToken)
    {
        // A buffered body sends Content-Length, which some gateways require.
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, requestTypeInfo));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        var message = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
        try
        {
            if (serverSentEvents)
            {
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            }

            await _settings.Headers.ApplyAsync(message, cancellationToken).ConfigureAwait(false);
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await GeminiErrorParser.FromResponseAsync(response, _settings.StreamIdleTimeout, cancellationToken).ConfigureAwait(false);
        }
    }
}
