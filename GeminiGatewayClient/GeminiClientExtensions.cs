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
        /// <param name="systemInstruction">Optional system instruction text.</param>
        /// <param name="generationConfig">Optional generation parameters.</param>
        /// <param name="model">The model to call, or <c>null</c> to use <see cref="IGeminiClient.DefaultModel"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The parsed response.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="prompt"/> is <c>null</c>, empty, or whitespace.</exception>
        /// <exception cref="InvalidOperationException">
        /// No model was specified and <see cref="IGeminiClient.DefaultModel"/> is <c>null</c>, or <see cref="GeminiClientOptions.ApiKeyProvider"/>
        /// returned an empty or invalid value.
        /// </exception>
        /// <exception cref="GeminiApiException">The endpoint returned a non-2xx status or an unreadable/error payload.</exception>
        /// <exception cref="TimeoutException">
        /// No response body bytes were received within <see cref="GeminiClientOptions.StreamIdleTimeout"/>.
        /// </exception>
        /// <exception cref="HttpRequestException">A transport-level network or TLS failure occurred before an HTTP response was received.</exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was canceled, or <see cref="HttpClient.Timeout"/> (<see cref="GeminiClientOptions.Timeout"/>) elapsed.
        /// </exception>
        public Task<GenerateContentResponse> GenerateContentAsync(
            string prompt,
            string? systemInstruction = null,
            GenerationConfig? generationConfig = null,
            string? model = null,
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
        /// <param name="systemInstruction">Optional system instruction text.</param>
        /// <param name="generationConfig">Optional generation parameters.</param>
        /// <param name="model">The model to call, or <c>null</c> to use <see cref="IGeminiClient.DefaultModel"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async stream of response chunks.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="prompt"/> is <c>null</c>, empty, or whitespace.</exception>
        /// <exception cref="InvalidOperationException">
        /// No model was specified and <see cref="IGeminiClient.DefaultModel"/> is <c>null</c>, or <see cref="GeminiClientOptions.ApiKeyProvider"/>
        /// returned an empty or invalid value during enumeration.
        /// </exception>
        /// <exception cref="GeminiApiException">
        /// Thrown during enumeration when the endpoint returns a non-2xx status, an in-stream <c>{"error": ...}</c> payload, or malformed JSON.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Thrown during enumeration when no chunk data is received within <see cref="GeminiClientOptions.StreamIdleTimeout"/>.
        /// </exception>
        /// <exception cref="HttpRequestException">
        /// Thrown during enumeration when a transport-level network or TLS failure occurs.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown during enumeration when <paramref name="cancellationToken"/> is canceled or <see cref="HttpClient.Timeout"/> (<see cref="GeminiClientOptions.Timeout"/>) elapses.
        /// </exception>
        public IAsyncEnumerable<GenerateContentResponse> StreamGenerateContentAsync(
            string prompt,
            string? systemInstruction = null,
            GenerationConfig? generationConfig = null,
            string? model = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            return client.StreamGenerateContentAsync(GenerateContentRequest.FromPrompt(prompt, systemInstruction, generationConfig), model, cancellationToken);
        }

        /// <summary>
        /// Sends a single text string to <c>:embedContent</c> and returns the embedding response.
        /// </summary>
        /// <param name="text">The text to embed.</param>
        /// <param name="taskType">Optional downstream task type hint.</param>
        /// <param name="title">Optional document title (only valid when <paramref name="taskType"/> is <see cref="TaskType.RetrievalDocument"/>).</param>
        /// <param name="outputDimensionality">Optional reduced output dimension.</param>
        /// <param name="model">The embedding model to call, or <c>null</c> to use <see cref="IGeminiClient.DefaultEmbeddingModel"/>.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The parsed embedding response.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="text"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The request parameters are invalid.</exception>
        /// <exception cref="InvalidOperationException">
        /// No model was specified and <see cref="IGeminiClient.DefaultEmbeddingModel"/> is <c>null</c>, or <see cref="GeminiClientOptions.ApiKeyProvider"/>
        /// returned an empty or invalid value.
        /// </exception>
        /// <exception cref="GeminiApiException">The endpoint returned a non-2xx status or an unreadable/error payload.</exception>
        /// <exception cref="TimeoutException">
        /// No response body bytes were received within <see cref="GeminiClientOptions.StreamIdleTimeout"/>.
        /// </exception>
        /// <exception cref="HttpRequestException">A transport-level network or TLS failure occurred before an HTTP response was received.</exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was canceled, or <see cref="HttpClient.Timeout"/> (<see cref="GeminiClientOptions.Timeout"/>) elapsed.
        /// </exception>
        public Task<EmbedContentResponse> EmbedContentAsync(
            string text,
            TaskType? taskType = null,
            string? title = null,
            int? outputDimensionality = null,
            string? model = null,
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
        /// your <c>JsonSerializerContext</c>. Defaults to <see cref="GeminiClientOptions.ToolSerializerOptions"/> (when <paramref name="client"/>
        /// is a <see cref="GeminiClient"/>) or <see cref="AIJsonUtilities.DefaultOptions"/>.
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
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="defaultModelDimensions"/> is less than or equal to zero.</exception>
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

        if (response.Candidates is [{ Content: { RawParts: { Count: > 0 } parts } candidateContent }, ..])
        {
            var hasMeaningfulPart = false;
            var needsNormalization = false;
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i] is not { } part || IsPureEmptyTextPart(part))
                {
                    needsNormalization = true;
                    continue;
                }

                hasMeaningfulPart = true;
                if (string.IsNullOrEmpty(part.Text) && HasNonTextPrimaryDataField(part))
                {
                    needsNormalization = true;
                }
            }

            if (hasMeaningfulPart)
            {
                if (!needsNormalization)
                {
                    content = candidateContent;
                    return true;
                }

                var normalized = candidateContent.Clone();
                var normalizedParts = new List<Part>(parts.Count);
                for (var i = 0; i < parts.Count; i++)
                {
                    if (parts[i] is not { } part || IsPureEmptyTextPart(part))
                    {
                        continue;
                    }

                    var clonedPart = part.Clone();
                    if (string.IsNullOrEmpty(clonedPart.Text) && HasNonTextPrimaryDataField(clonedPart))
                    {
                        clonedPart.Text = null;
                    }

                    normalizedParts.Add(clonedPart);
                }

                normalized.Parts = normalizedParts;
                content = normalized;
                return true;
            }
        }

        content = null;
        return false;
    }

    /// <summary>
    /// Aggregates a sequence of streamed <see cref="GenerateContentResponse"/> chunks into a single model <see cref="Content"/>
    /// suitable for multi-turn conversation history, coalescing consecutive thought parts, consecutive text parts, and
    /// trailing <c>thoughtSignature</c> parts while omitting empty placeholder chunks.
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

        var aggregatedParts = new List<Part>();
        var role = Role.Model;
        StringBuilder? textBuilder = null;
        Part? activeBuilderPart = null;

        foreach (var chunk in chunks)
        {
            if (chunk?.Candidates is not [{ Content: { } content }, ..])
            {
                continue;
            }

            if (content.Role is { } contentRole)
            {
                role = contentRole;
            }

            if (content.RawParts is not { Count: > 0 } parts)
            {
                continue;
            }

            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i] is { } part)
                {
                    AppendAggregatedPart(aggregatedParts, part, ref activeBuilderPart, ref textBuilder);
                }
            }
        }

        FlushTextBuilder(ref activeBuilderPart, textBuilder);

        return aggregatedParts.Count > 0
            ? new Content { Role = role, Parts = aggregatedParts }
            : null;
    }

    /// <summary>
    /// Asynchronously consumes a stream of <see cref="GenerateContentResponse"/> chunks and aggregates them into a single
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

        var aggregatedParts = new List<Part>();
        var role = Role.Model;
        StringBuilder? textBuilder = null;
        Part? activeBuilderPart = null;

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk?.Candidates is not [{ Content: { } content }, ..])
            {
                continue;
            }

            if (content.Role is { } contentRole)
            {
                role = contentRole;
            }

            if (content.RawParts is not { Count: > 0 } parts)
            {
                continue;
            }

            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i] is { } part)
                {
                    AppendAggregatedPart(aggregatedParts, part, ref activeBuilderPart, ref textBuilder);
                }
            }
        }

        FlushTextBuilder(ref activeBuilderPart, textBuilder);

        return aggregatedParts.Count > 0
            ? new Content { Role = role, Parts = aggregatedParts }
            : null;
    }

    private static void AppendAggregatedPart(
        List<Part> aggregatedParts,
        Part part,
        ref Part? activeBuilderPart,
        ref StringBuilder? textBuilder)
    {
        if (IsPureEmptyTextPart(part))
        {
            return;
        }

        var isTextOnly = part.InlineData is null &&
                         part.FileData is null &&
                         part.FunctionCall is null &&
                         part.FunctionResponse is null &&
                         part.ExecutableCode is null &&
                         part.CodeExecutionResult is null &&
                         part.VideoMetadata is null &&
                         (part.ExtensionData is null || part.ExtensionData.Count == 0);

        if (isTextOnly)
        {
            // Signature-only carrier chunk (e.g., {"text": "", "thoughtSignature": "..."} or {"thoughtSignature": "..."})
            if (string.IsNullOrEmpty(part.Text) && !string.IsNullOrEmpty(part.ThoughtSignature))
            {
                if (aggregatedParts.Count > 0)
                {
                    var last = aggregatedParts[^1];
                    if (string.IsNullOrEmpty(last.ThoughtSignature))
                    {
                        FlushTextBuilder(ref activeBuilderPart, textBuilder);
                        last.ThoughtSignature = part.ThoughtSignature;
                        return;
                    }

                    if (string.Equals(last.ThoughtSignature, part.ThoughtSignature, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                FlushTextBuilder(ref activeBuilderPart, textBuilder);
                aggregatedParts.Add(new Part { ThoughtSignature = part.ThoughtSignature });
                return;
            }

            // Thought text chunk or normal text chunk: coalesce consecutive chunks of the same kind
            // only while the preceding part has not yet been closed with a thoughtSignature.
            if (!string.IsNullOrEmpty(part.Text) && aggregatedParts.Count > 0)
            {
                var last = aggregatedParts[^1];
                var lastIsSameTextKind =
                    last.Text is not null &&
                    (last.Thought == true) == (part.Thought == true) &&
                    last.InlineData is null &&
                    last.FileData is null &&
                    last.FunctionCall is null &&
                    last.FunctionResponse is null &&
                    last.ExecutableCode is null &&
                    last.CodeExecutionResult is null &&
                    last.VideoMetadata is null &&
                    (last.ExtensionData is null || last.ExtensionData.Count == 0) &&
                    string.IsNullOrEmpty(last.ThoughtSignature);

                if (lastIsSameTextKind)
                {
                    if (!ReferenceEquals(activeBuilderPart, last))
                    {
                        FlushTextBuilder(ref activeBuilderPart, textBuilder);
                        textBuilder ??= new StringBuilder(Math.Max(64, last.Text!.Length + part.Text.Length));
                        textBuilder.Clear();
                        textBuilder.Append(last.Text);
                        activeBuilderPart = last;
                    }

                    textBuilder!.Append(part.Text);
                    if (!string.IsNullOrEmpty(part.ThoughtSignature))
                    {
                        last.ThoughtSignature = part.ThoughtSignature;
                        FlushTextBuilder(ref activeBuilderPart, textBuilder);
                    }

                    return;
                }
            }
        }

        FlushTextBuilder(ref activeBuilderPart, textBuilder);

        var cloned = part.Clone();
        if (string.IsNullOrEmpty(cloned.Text) && HasNonTextPrimaryDataField(cloned))
        {
            cloned.Text = null;
        }

        aggregatedParts.Add(cloned);
    }

    private static void FlushTextBuilder(ref Part? activeBuilderPart, StringBuilder? textBuilder)
    {
        if (activeBuilderPart is not null && textBuilder is not null)
        {
            activeBuilderPart.Text = textBuilder.ToString();
            activeBuilderPart = null;
            textBuilder.Clear();
        }
    }

    private static bool HasNonTextPrimaryDataField(Part part) =>
        part.InlineData is not null ||
        part.FileData is not null ||
        part.FunctionCall is not null ||
        part.FunctionResponse is not null ||
        part.ExecutableCode is not null ||
        part.CodeExecutionResult is not null;

    private static bool IsPureEmptyTextPart(Part part) =>
        string.IsNullOrEmpty(part.Text) &&
        string.IsNullOrEmpty(part.ThoughtSignature) &&
        part.InlineData is null &&
        part.FileData is null &&
        part.FunctionCall is null &&
        part.FunctionResponse is null &&
        part.ExecutableCode is null &&
        part.CodeExecutionResult is null &&
        part.VideoMetadata is null &&
        (part.ExtensionData is null || part.ExtensionData.Count == 0);
}
