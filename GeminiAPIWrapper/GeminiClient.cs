using System.Buffers;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GeminiAPIWrapper.Http;
using GeminiAPIWrapper.Models;
using GeminiAPIWrapper.Serialization;

namespace GeminiAPIWrapper;

/// <summary>
/// HTTP client for the Gemini (Generative Language) API that works directly against Google or behind API gateways and proxies.
/// <para>
/// Headers are applied per <see cref="HttpRequestMessage"/> rather than on <see cref="HttpClient.DefaultRequestHeaders"/>,
/// so a shared <see cref="HttpClient"/> (for example from <c>IHttpClientFactory</c>) is never mutated.
/// Use <see cref="GeminiClientExtensions.AsIChatClient(IGeminiClient, string?, JsonSerializerOptions?)"/> to obtain a
/// <c>Microsoft.Extensions.AI</c> <c>IChatClient</c>.
/// </para>
/// </summary>
public sealed class GeminiClient : IGeminiClient, IDisposable
{
    private const string GenerateContentOperation = "generateContent";
    private const string StreamGenerateContentOperation = "streamGenerateContent";

    private static readonly TimeSpan s_pooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private readonly GeminiClientSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

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

    internal TimeSpan HttpClientTimeout => _httpClient.Timeout;

    /// <inheritdoc />
    public async Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var requestUri = PrepareRequest(request, model, GenerateContentOperation, serverSentEvents: false);

        using var httpRequest = await CreateRequestMessageAsync(requestUri, request, serverSentEvents: false, cancellationToken).ConfigureAwait(false);
        using var httpResponse = await SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        using var watchdog = new StreamIdleWatchdog(_settings.StreamIdleTimeout, cancellationToken);

        try
        {
            var stream = await httpResponse.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new ArrayBufferWriter<byte>(1024);
                var rented = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    while (true)
                    {
                        int read;
                        watchdog.BeforeRead();
                        try
                        {
                            read = await stream.ReadAsync(rented.AsMemory(), watchdog.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            watchdog.AfterRead();
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        rented.AsSpan(0, read).CopyTo(buffer.GetSpan(read));
                        buffer.Advance(read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                return GeminiStreamReader.DeserializeUtf8Response(
                    buffer.WrittenSpan,
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

    /// <summary>
    /// Disposes the internal <see cref="HttpClient"/> when it was created by this instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async IAsyncEnumerable<GenerateContentResponse> StreamCoreAsync(
        Uri requestUri,
        GenerateContentRequest request,
        bool serverSentEvents,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = await CreateRequestMessageAsync(requestUri, request, serverSentEvents, cancellationToken).ConfigureAwait(false);
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

    private Uri PrepareRequest(GenerateContentRequest request, string? model, string operation, bool serverSentEvents)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(nameof(request));
        return _settings.Endpoint.GetOperationUri(model, operation, serverSentEvents);
    }

    private ValueTask<HttpRequestMessage> CreateRequestMessageAsync(
        Uri requestUri,
        GenerateContentRequest request,
        bool serverSentEvents,
        CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(request, GeminiJsonContext.Default.GenerateContentRequest);

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
                watchdog.BeforeRead();
                try
                {
                    throw await GeminiErrorParser.CreateFromResponseAsync(response, watchdog.Token).ConfigureAwait(false);
                }
                finally
                {
                    watchdog.AfterRead();
                }
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
