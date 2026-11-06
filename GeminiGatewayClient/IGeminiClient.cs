using System.Text.Json;
using GeminiGatewayClient.Models;

namespace GeminiGatewayClient;

/// <summary>
/// Client for the Gemini content generation and embedding endpoints.
/// <para>
/// Depend on this interface (rather than <see cref="GeminiClient"/>) to substitute a fake in unit tests.
/// Convenience overloads such as <see cref="GeminiClientExtensions.GenerateContentAsync(IGeminiClient, string, string?, GenerationConfig?, string?, CancellationToken)"/>,
/// <see cref="GeminiClientExtensions.EmbedContentAsync(IGeminiClient, string, TaskType?, string?, int?, string?, CancellationToken)"/>,
/// <see cref="GeminiClientExtensions.AsIChatClient(IGeminiClient, string?, System.Text.Json.JsonSerializerOptions?)"/>, and
/// <see cref="GeminiClientExtensions.AsIEmbeddingGenerator(IGeminiClient, string?, int?)"/> are provided as extension methods.
/// </para>
/// </summary>
public interface IGeminiClient
{
    /// <summary>
    /// The endpoint that model resources are resolved against.
    /// </summary>
    Uri Endpoint { get; }

    /// <summary>
    /// The model used when a content-generation call does not specify one, or <c>null</c>.
    /// </summary>
    string? DefaultModel { get; }

    /// <summary>
    /// The model used when an embedding call does not specify one (<see cref="GeminiClientOptions.EmbeddingModel"/>,
    /// falling back to <see cref="DefaultModel"/>), or <c>null</c>.
    /// </summary>
    string? DefaultEmbeddingModel => DefaultModel;

    /// <summary>
    /// Serializer options for tool arguments and results (<see cref="GeminiClientOptions.ToolSerializerOptions"/>), or <c>null</c>.
    /// A decorator should forward this so its chat client keeps the caller's options.
    /// </summary>
    JsonSerializerOptions? ToolSerializerOptions => null;

    /// <summary>
    /// Calls <c>:generateContent</c> and returns the complete response.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="model">The model to call, or <c>null</c> to use <see cref="DefaultModel"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="request"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">
    /// No model was specified and <see cref="DefaultModel"/> is <c>null</c>, or <see cref="GeminiClientOptions.ApiKeyProvider"/>
    /// returned an empty or invalid value.
    /// </exception>
    /// <exception cref="GeminiApiException">The endpoint returned a non-2xx status or an unreadable/error payload.</exception>
    /// <exception cref="HttpRequestException">A transport-level network or TLS failure occurred, including the connection being lost while reading a successful response body.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled, or <see cref="HttpClient.Timeout"/> (<see cref="GeminiClientOptions.Timeout"/>) elapsed.
    /// </exception>
    Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        string? model = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <c>:streamGenerateContent</c> and yields response chunks as they arrive.
    /// Arguments are validated when this method is called, not on first enumeration.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="model">The model to call, or <c>null</c> to use <see cref="DefaultModel"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of response chunks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="request"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">
    /// No model was specified and <see cref="DefaultModel"/> is <c>null</c>, or <see cref="GeminiClientOptions.ApiKeyProvider"/>
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
    IAsyncEnumerable<GenerateContentResponse> StreamGenerateContentAsync(
        GenerateContentRequest request,
        string? model = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <c>:embedContent</c> to generate a vector embedding for a single <see cref="Content"/> item.
    /// </summary>
    /// <param name="request">The embedding request to send.</param>
    /// <param name="model">The embedding model to call, or <c>null</c> to use <see cref="DefaultEmbeddingModel"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed embedding response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="request"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">
    /// No model was specified and <see cref="DefaultEmbeddingModel"/> is <c>null</c>, or <see cref="GeminiClientOptions.ApiKeyProvider"/>
    /// returned an empty or invalid value.
    /// </exception>
    /// <exception cref="GeminiApiException">The endpoint returned a non-2xx status or an unreadable/error payload.</exception>
    /// <exception cref="HttpRequestException">A transport-level network or TLS failure occurred, including the connection being lost while reading a successful response body.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled, or <see cref="HttpClient.Timeout"/> (<see cref="GeminiClientOptions.Timeout"/>) elapsed.
    /// </exception>
    Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        string? model = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <c>:batchEmbedContents</c> to generate vector embeddings for multiple <see cref="EmbedContentRequest"/> items in a single HTTP request.
    /// When an item's <see cref="EmbedContentRequest.Model"/> is omitted or specified as a bare model ID, it is normalized automatically to
    /// <c>models/{model}</c> on the wire without mutating <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The batch embedding request to send.</param>
    /// <param name="model">The embedding model to call, or <c>null</c> to use the first item's model, then <see cref="DefaultEmbeddingModel"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed batch embedding response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="request"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">
    /// No model was specified and <see cref="DefaultEmbeddingModel"/> is <c>null</c>, or <see cref="GeminiClientOptions.ApiKeyProvider"/>
    /// returned an empty or invalid value.
    /// </exception>
    /// <exception cref="GeminiApiException">The endpoint returned a non-2xx status or an unreadable/error payload.</exception>
    /// <exception cref="HttpRequestException">A transport-level network or TLS failure occurred, including the connection being lost while reading a successful response body.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled, or <see cref="HttpClient.Timeout"/> (<see cref="GeminiClientOptions.Timeout"/>) elapsed.
    /// </exception>
    Task<BatchEmbedContentsResponse> BatchEmbedContentsAsync(
        BatchEmbedContentsRequest request,
        string? model = null,
        CancellationToken cancellationToken = default);
}
