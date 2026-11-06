using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient;

/// <summary>
/// Convenience extension methods for <see cref="IGeminiClient"/> and <see cref="GenerateContentResponse"/>.
/// </summary>
public static class GeminiClientExtensions
{
    /// <param name="client">The Gemini client.</param>
    extension(IGeminiClient client)
    {
        /// <summary>
        /// Sends a single user prompt to <c>:generateContent</c> and returns the complete response.
        /// </summary>
        /// <param name="prompt">The user prompt text.</param>
        /// <param name="model">The model to call, or <c>null</c> to use <see cref="IGeminiClient.DefaultModel"/>.</param>
        /// <param name="systemInstruction">Optional system instruction text.</param>
        /// <param name="generationConfig">Optional generation parameters.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The parsed response.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="prompt"/> is <c>null</c>, empty, or whitespace.</exception>
        /// <exception cref="InvalidOperationException">
        /// No model was specified and <see cref="IGeminiClient.DefaultModel"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="GeminiApiException">The endpoint returned a non-2xx status or an unreadable/error payload.</exception>
        /// <exception cref="HttpRequestException">A transport-level network or TLS failure occurred, including the connection being lost while reading a successful response body.</exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was canceled, or <see cref="HttpClient.Timeout"/> elapsed.
        /// </exception>
        public Task<GenerateContentResponse> GenerateContentAsync(
            string prompt,
            string? model = null,
            string? systemInstruction = null,
            GenerationConfig? generationConfig = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.GenerateContentAsync(GenerateContentRequest.FromPrompt(prompt, systemInstruction, generationConfig), model, cancellationToken);
        }

        /// <summary>
        /// Sends a single user prompt to <c>:streamGenerateContent</c> and yields response chunks as they arrive.
        /// Arguments are validated when this method is called, not on first enumeration.
        /// </summary>
        /// <param name="prompt">The user prompt text.</param>
        /// <param name="model">The model to call, or <c>null</c> to use <see cref="IGeminiClient.DefaultModel"/>.</param>
        /// <param name="systemInstruction">Optional system instruction text.</param>
        /// <param name="generationConfig">Optional generation parameters.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async stream of response chunks.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="prompt"/> is <c>null</c>, empty, or whitespace.</exception>
        /// <exception cref="InvalidOperationException">
        /// No model was specified and <see cref="IGeminiClient.DefaultModel"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="GeminiApiException">
        /// Thrown during enumeration when the endpoint returns a non-2xx status, an in-stream <c>{"error": ...}</c> payload, or malformed JSON.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Thrown during enumeration when no data is received within <see cref="GeminiClientOptions.StreamIdleTimeout"/>.
        /// </exception>
        /// <exception cref="HttpRequestException">
        /// Thrown during enumeration when a transport-level network or TLS failure occurs.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown during enumeration when <paramref name="cancellationToken"/> is canceled or <see cref="HttpClient.Timeout"/> elapses.
        /// </exception>
        public IAsyncEnumerable<GenerateContentResponse> StreamGenerateContentAsync(
            string prompt,
            string? model = null,
            string? systemInstruction = null,
            GenerationConfig? generationConfig = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.StreamGenerateContentAsync(GenerateContentRequest.FromPrompt(prompt, systemInstruction, generationConfig), model, cancellationToken);
        }

        /// <summary>
        /// Sends a single text string to <c>:embedContent</c> and returns the embedding response.
        /// </summary>
        /// <param name="text">The text to embed.</param>
        /// <param name="model">The embedding model to call, or <c>null</c> to use <see cref="IGeminiClient.DefaultEmbeddingModel"/>.</param>
        /// <param name="taskType">Optional downstream task type hint.</param>
        /// <param name="title">Optional document title (only valid when <paramref name="taskType"/> is <see cref="TaskType.RetrievalDocument"/>).</param>
        /// <param name="outputDimensionality">Optional reduced output dimension.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The parsed embedding response.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="text"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">
        /// No model was specified and <see cref="IGeminiClient.DefaultEmbeddingModel"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="GeminiApiException">The endpoint returned a non-2xx status or an unreadable/error payload.</exception>
        /// <exception cref="HttpRequestException">A transport-level network or TLS failure occurred, including the connection being lost while reading a successful response body.</exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was canceled, or <see cref="HttpClient.Timeout"/> elapsed.
        /// </exception>
        public Task<EmbedContentResponse> EmbedContentAsync(
            string text,
            string? model = null,
            TaskType? taskType = null,
            string? title = null,
            int? outputDimensionality = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.EmbedContentAsync(
                EmbedContentRequest.FromText(text, taskType, title, outputDimensionality),
                model,
                cancellationToken);
        }

        /// <summary>
        /// Wraps <paramref name="client"/> in a <c>Microsoft.Extensions.AI</c> <see cref="IChatClient"/>.
        /// The returned chat client does not own <paramref name="client"/>; dispose the <see cref="GeminiClient"/> separately.
        /// </summary>
        /// <param name="defaultModelId">The model used when <see cref="ChatOptions.ModelId"/> is not set; defaults to <see cref="IGeminiClient.DefaultModel"/>.</param>
        /// <param name="toolSerializerOptions">
        /// Serializer options for function arguments and results of custom types. In NativeAOT applications, supply options backed by
        /// your <c>JsonSerializerContext</c>. Defaults to <see cref="AIJsonUtilities.DefaultOptions"/>.
        /// </param>
        /// <returns>An <see cref="IChatClient"/> backed by <paramref name="client"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
        public IChatClient AsIChatClient(
            string? defaultModelId = null,
            JsonSerializerOptions? toolSerializerOptions = null) =>
            new GeminiChatClient(client, defaultModelId, toolSerializerOptions);

        /// <summary>
        /// Wraps <paramref name="client"/> in a <c>Microsoft.Extensions.AI</c> <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>.
        /// The returned generator does not own <paramref name="client"/>; dispose the <see cref="GeminiClient"/> separately.
        /// </summary>
        /// <param name="defaultModelId">
        /// The model used when <see cref="EmbeddingGenerationOptions.ModelId"/> is not set; defaults to <see cref="IGeminiClient.DefaultEmbeddingModel"/>.
        /// </param>
        /// <param name="defaultModelDimensions">
        /// Optional default output dimensionality used when <see cref="EmbeddingGenerationOptions.Dimensions"/> is not set.
        /// </param>
        /// <returns>An <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> backed by <paramref name="client"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
        public IEmbeddingGenerator<string, Embedding<float>> AsIEmbeddingGenerator(
            string? defaultModelId = null,
            int? defaultModelDimensions = null) =>
            new GeminiEmbeddingGenerator(client, defaultModelId, defaultModelDimensions);
    }

    /// <summary>
    /// Extracts the primary candidate's <see cref="Content"/> when it contains at least one non-empty <see cref="Part"/>
    /// suitable for appending to a multi-turn <see cref="GenerateContentRequest.Contents"/> history.
    /// Returns <c>false</c> when the candidate has no parts (for example when a thinking model exhausts <c>MaxOutputTokens</c>
    /// before emitting output tokens).
    /// </summary>
    /// <param name="response">The response to inspect.</param>
    /// <param name="content">When this method returns <c>true</c>, contains the non-empty model <see cref="Content"/>.</param>
    /// <returns><c>true</c> if a valid model <see cref="Content"/> with at least one non-empty part is present; otherwise <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <c>null</c>.</exception>
    public static bool TryGetModelContent(this GenerateContentResponse response, [NotNullWhen(true)] out Content? content)
    {
        ArgumentNullException.ThrowIfNull(response);

        content = ToModelContent([response]);
        return content is not null;
    }

    /// <summary>
    /// Collects the parts of streamed <see cref="GenerateContentResponse"/> chunks into a single model <see cref="Content"/>
    /// suitable for multi-turn conversation history, omitting empty placeholder parts. Parts keep their boundaries
    /// because Gemini forbids merging a part that carries a <c>thoughtSignature</c> with another part.
    /// </summary>
    /// <param name="chunks">The streamed response chunks.</param>
    /// <returns>
    /// A single <see cref="Content"/> with <see cref="Content.Role"/> set to <see cref="Role.Model"/>, or <c>null</c>
    /// if the chunks contained no meaningful parts.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="chunks"/> is <c>null</c>.</exception>
    public static Content? ToModelContent(this IEnumerable<GenerateContentResponse> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var parts = chunks
            .SelectMany(chunk => chunk?.Candidates is [{ Content: { } content }, ..] ? content.Parts : [])
            .Where(part => part is { IsEmptyText: false })
            .ToList();

        // Gemini rejects text next to other data on a part, even when that text is empty.
        foreach (var part in parts.Where(part => part is { Text: "", HasNonTextData: true }))
        {
            part.Text = null;
        }

        return parts.Count > 0 ? new Content { Role = Role.Model, Parts = parts } : null;
    }

    /// <summary>
    /// Asynchronously consumes a stream of <see cref="GenerateContentResponse"/> chunks and collects them into a single
    /// model <see cref="Content"/> suitable for multi-turn conversation history.
    /// </summary>
    /// <param name="chunks">The async stream of response chunks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A single <see cref="Content"/> with <see cref="Content.Role"/> set to <see cref="Role.Model"/>, or <c>null</c>
    /// if the stream contained no meaningful parts.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="chunks"/> is <c>null</c>.</exception>
    public static async ValueTask<Content?> ToModelContentAsync(
        this IAsyncEnumerable<GenerateContentResponse> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var received = new List<GenerateContentResponse>();
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            received.Add(chunk);
        }

        return received.ToModelContent();
    }
}
