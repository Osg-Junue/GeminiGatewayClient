using System.Text.Json;
using GeminiAPIWrapper.Models;
using Microsoft.Extensions.AI;

namespace GeminiAPIWrapper;

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
        /// your <c>JsonSerializerContext</c>. Defaults to <see cref="AIJsonUtilities.DefaultOptions"/>.
        /// </param>
        /// <returns>An <see cref="IChatClient"/> backed by <paramref name="client"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
        public IChatClient AsIChatClient(
            string? defaultModelId = null,
            JsonSerializerOptions? toolSerializerOptions = null) =>
            new GeminiChatClient(client, defaultModelId, toolSerializerOptions);
    }
}

