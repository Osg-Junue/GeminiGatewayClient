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
    /// <exception cref="InvalidOperationException">No model was specified and <see cref="DefaultModel"/> is <c>null</c>.</exception>
    /// <exception cref="GeminiApiException">The endpoint returned an error status or an unreadable payload.</exception>
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
    /// <exception cref="InvalidOperationException">No model was specified and <see cref="DefaultModel"/> is <c>null</c>.</exception>
    IAsyncEnumerable<GenerateContentResponse> StreamGenerateContentAsync(
        GenerateContentRequest request,
        string? model = null,
        CancellationToken cancellationToken = default);
}
