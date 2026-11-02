using System.Runtime.CompilerServices;
using System.Text.Json;
using GeminiAPIWrapper.Configurations;
using Microsoft.Extensions.AI;

namespace GeminiAPIWrapper;

/// <summary>
/// High-level client for the Gemini API. Provides both direct Gemini request/response methods
/// and an implementation of <see cref="IChatClient"/> from <c>Microsoft.Extensions.AI</c>.
/// <para>
/// For unit testing, either supply an <see cref="HttpClient"/> backed by a fake <see cref="HttpMessageHandler"/>
/// via <see cref="GeminiServiceBuilder.Build(Options.GenerativeLanguageOptions, HttpClient)"/> or mock <see cref="IChatClient"/>.
/// </para>
/// </summary>
public sealed class GeminiService : IChatClient, IDisposable
{
    private readonly IGeminiClient _client;
    private readonly ChatClientMetadata _metadata;
    private readonly JsonSerializerOptions? _serializerOptions;

    /// <summary>
    /// Initializes a new instance of <see cref="GeminiService"/>.
    /// Use <see cref="GeminiServiceBuilder"/> to create instances in application code.
    /// </summary>
    /// <param name="client">Underlying transport client.</param>
    /// <param name="endpointBase">Optional endpoint base URL used to populate <see cref="ChatClientMetadata"/>.</param>
    /// <param name="serializerOptions">Optional serializer options for <see cref="IChatClient"/> tool result serialization.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
    internal GeminiService(IGeminiClient client, string? endpointBase = null, JsonSerializerOptions? serializerOptions = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _metadata = GeminiChatClientMapper.CreateMetadata(endpointBase);
        _serializerOptions = serializerOptions;
    }

    #region Gemini API Methods

    /// <summary>
    /// Sends a single user message to the Gemini API and returns the complete response.
    /// </summary>
    /// <param name="userMessage">The user prompt text.</param>
    /// <param name="systemInstruction">Optional system instruction text.</param>
    /// <param name="generationConfig">Optional generation parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed <see cref="GeminiResponse"/>.</returns>
    public async Task<GeminiResponse?> GenerateAsync(
        string userMessage,
        string systemInstruction = "",
        GenerationConfig? generationConfig = default,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        var request = BuildRequest(userMessage, systemInstruction, generationConfig);
        return await _client.GenerateAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a custom <see cref="GeminiRequest"/> to the Gemini API and returns the complete response.
    /// </summary>
    /// <param name="request">The <see cref="GeminiRequest"/> to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed <see cref="GeminiResponse"/>.</returns>
    public async Task<GeminiResponse?> GenerateAsync(GeminiRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _client.GenerateAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a single user message to the Gemini streaming endpoint and yields response chunks as they arrive.
    /// </summary>
    /// <param name="userMessage">The user prompt text.</param>
    /// <param name="systemInstruction">Optional system instruction text.</param>
    /// <param name="generationConfig">Optional generation parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async stream of <see cref="GeminiResponse"/> chunks.</returns>
    public async IAsyncEnumerable<GeminiResponse?> StreamGenerateAsync(
        string userMessage,
        string systemInstruction = "",
        GenerationConfig? generationConfig = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        var request = BuildRequest(userMessage, systemInstruction, generationConfig);
        await foreach (var response in _client.StreamGenerateAsync(request, ct).ConfigureAwait(false))
        {
            yield return response;
        }
    }

    /// <summary>
    /// Sends a custom <see cref="GeminiRequest"/> to the Gemini streaming endpoint and yields response chunks as they arrive.
    /// </summary>
    /// <param name="request">The <see cref="GeminiRequest"/> to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async stream of <see cref="GeminiResponse"/> chunks.</returns>
    public async IAsyncEnumerable<GeminiResponse?> StreamGenerateAsync(
        GeminiRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await foreach (var response in _client.StreamGenerateAsync(request, ct).ConfigureAwait(false))
        {
            yield return response;
        }
    }

    #endregion

    #region IChatClient Implementation

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = GeminiChatClientMapper.ToGeminiRequest(messages, options, this, _serializerOptions);
        var modelOverride = options?.ModelId;
        var response = await _client.GenerateAsync(request, cancellationToken, modelOverride).ConfigureAwait(false);
        return GeminiChatClientMapper.ToChatResponse(response, modelOverride ?? _metadata.DefaultModelId);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = GeminiChatClientMapper.ToGeminiRequest(messages, options, this, _serializerOptions);
        var modelOverride = options?.ModelId;
        await foreach (var chunk in _client.StreamGenerateAsync(request, cancellationToken, modelOverride).ConfigureAwait(false))
        {
            if (chunk is not null)
            {
                yield return GeminiChatClientMapper.ToChatResponseUpdate(chunk, modelOverride ?? _metadata.DefaultModelId);
            }
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return null;
    }

    /// <summary>
    /// Disposes the underlying client if it owns an <see cref="HttpClient"/>.
    /// </summary>
    public void Dispose()
    {
        if (_client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    #endregion

    #region Private Methods

    private static GeminiRequest BuildRequest(string userMessage, string systemInstruction, GenerationConfig? generationConfig)
    {
        var request = new GeminiRequest
        {
            Contents =
            [
                new Content
                {
                    Role = Role.User,
                    Parts = [new Part { Text = userMessage }]
                }
            ]
        };

        if (!string.IsNullOrEmpty(systemInstruction))
        {
            request.SystemInstruction = new SystemInstruction
            {
                Parts = [new Part { Text = systemInstruction }]
            };
        }

        if (generationConfig is not null)
        {
            request.GenerationConfig = generationConfig;
        }

        return request;
    }

    #endregion
}
