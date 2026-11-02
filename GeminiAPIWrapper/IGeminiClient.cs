namespace GeminiAPIWrapper;

/// <summary>
/// Low-level transport for the Gemini content generation endpoints.
/// </summary>
internal interface IGeminiClient
{
    /// <summary>
    /// Calls <c>:generateContent</c> and returns the complete response.
    /// </summary>
    Task<GeminiResponse?> GenerateAsync(
        GeminiRequest request,
        CancellationToken cancellationToken = default,
        string? modelOverride = null);

    /// <summary>
    /// Calls <c>:streamGenerateContent</c> and yields response chunks as they arrive.
    /// </summary>
    IAsyncEnumerable<GeminiResponse?> StreamGenerateAsync(
        GeminiRequest request,
        CancellationToken cancellationToken = default,
        string? modelOverride = null);
}
