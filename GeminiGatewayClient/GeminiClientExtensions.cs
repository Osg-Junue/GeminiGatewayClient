using System.Text.Json;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient;

/// <summary>
/// Convenience extension methods for <see cref="IGeminiClient"/>.
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
    }
}

