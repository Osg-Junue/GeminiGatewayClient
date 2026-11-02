using System.Runtime.CompilerServices;
using System.Text.Json;
using GeminiAPIWrapper.Chat;
using GeminiAPIWrapper.Models;
using Microsoft.Extensions.AI;

namespace GeminiAPIWrapper;

/// <summary>
/// <c>Microsoft.Extensions.AI</c> <see cref="IChatClient"/> implementation backed by an <see cref="IGeminiClient"/>.
/// Supports multi-turn chat, streaming, tool calling (<c>FunctionInvokingChatClient</c>), structured output,
/// per-request model overrides (<see cref="ChatOptions.ModelId"/>), and reasoning options (<see cref="ChatOptions.Reasoning"/>).
/// <para>
/// Create instances with <see cref="GeminiClientExtensions.AsIChatClient(IGeminiClient, string?, JsonSerializerOptions?)"/>.
/// This type does not own the underlying <see cref="IGeminiClient"/>; disposing it is a no-op.
/// </para>
/// </summary>
public sealed class GeminiChatClient : IChatClient
{
    private readonly IGeminiClient _client;
    private readonly JsonSerializerOptions _toolSerializerOptions;
    private readonly ChatClientMetadata _metadata;

    /// <summary>
    /// Initializes a new <see cref="GeminiChatClient"/>.
    /// </summary>
    /// <param name="client">The underlying Gemini client.</param>
    /// <param name="defaultModelId">The model used when <see cref="ChatOptions.ModelId"/> is not set; defaults to <see cref="IGeminiClient.DefaultModel"/>.</param>
    /// <param name="toolSerializerOptions">
    /// Serializer options for function arguments and results; defaults to <see cref="GeminiClientOptions.ToolSerializerOptions"/>
    /// (when <paramref name="client"/> is a <see cref="GeminiClient"/>) or <see cref="AIJsonUtilities.DefaultOptions"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
    public GeminiChatClient(IGeminiClient client, string? defaultModelId = null, JsonSerializerOptions? toolSerializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _toolSerializerOptions = toolSerializerOptions
            ?? (client as GeminiClient)?.ToolSerializerOptions
            ?? AIJsonUtilities.DefaultOptions;
        _metadata = new ChatClientMetadata("gemini", client.Endpoint, string.IsNullOrWhiteSpace(defaultModelId) ? client.DefaultModel : defaultModelId);
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var model = ResolveModel(options);
        var request = ChatRequestMapper.ToRequest(messages, options, this, model, _toolSerializerOptions);
        var response = await _client.GenerateContentAsync(request, model, cancellationToken).ConfigureAwait(false);
        return ChatResponseMapper.ToChatResponse(response, model);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Token usage is reported once, as a trailing update containing a single <see cref="UsageContent"/> with the final
    /// cumulative counts (Gemini repeats cumulative <c>usageMetadata</c> on streamed chunks).
    /// </remarks>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Map and validate eagerly so argument/option errors surface at call time rather than on first enumeration.
        var model = ResolveModel(options);
        var request = ChatRequestMapper.ToRequest(messages, options, this, model, _toolSerializerOptions);
        var chunks = _client.StreamGenerateContentAsync(request, model, cancellationToken);
        return StreamCoreAsync(chunks, model, cancellationToken);
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

        return serviceType.IsInstanceOfType(_client) ? _client : null;
    }

    /// <summary>
    /// No-op: the underlying <see cref="IGeminiClient"/> is not owned by this instance.
    /// </summary>
    public void Dispose()
    {
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamCoreAsync(
        IAsyncEnumerable<GenerateContentResponse> chunks,
        string? model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        GenerateContentResponse? lastUsageChunk = null;

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.UsageMetadata is not null)
            {
                lastUsageChunk = chunk;
            }

            yield return ChatResponseMapper.ToChatResponseUpdate(chunk, model);
        }

        if (ChatResponseMapper.ToUsageUpdate(lastUsageChunk, model) is { } usageUpdate)
        {
            yield return usageUpdate;
        }
    }

    private string? ResolveModel(ChatOptions? options) =>
        string.IsNullOrWhiteSpace(options?.ModelId) ? _metadata.DefaultModelId : options.ModelId;
}
