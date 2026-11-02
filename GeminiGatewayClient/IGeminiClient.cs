using GeminiGatewayClient.Models;

namespace GeminiGatewayClient;

/// <summary>
/// Client for the Gemini content generation endpoints.
/// <para>
/// Depend on this interface (rather than <see cref="GeminiClient"/>) to substitute a fake in unit tests.
/// Convenience overloads such as <see cref="GeminiClientExtensions.GenerateContentAsync(IGeminiClient, string, string?, GenerationConfig?, string?, CancellationToken)"/>
/// and <see cref="GeminiClientExtensions.AsIChatClient(IGeminiClient, string?, System.Text.Json.JsonSerializerOptions?)"/> are provided as extension methods.
/// </para>
/// </summary>
public interface IGeminiClient
{
    /// <summary>
    /// The endpoint that model resources are resolved against.
    /// </summary>
    Uri Endpoint { get; }

    /// <summary>
    /// The model used when a call does not specify one, or <c>null</c>.
    /// </summary>
    string? DefaultModel { get; }

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
    /// <exception cref="TimeoutException">
    /// No response body bytes were received within <see cref="GeminiClientOptions.StreamIdleTimeout"/>.
    /// </exception>
    /// <exception cref="HttpRequestException">A transport-level network or TLS failure occurred before an HTTP response was received.</exception>
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
}
