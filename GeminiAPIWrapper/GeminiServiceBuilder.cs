using GeminiAPIWrapper.Options;

namespace GeminiAPIWrapper;

/// <summary>
/// Factory methods for creating <see cref="GeminiService"/> instances configured for the Gemini API
/// (directly or behind an API gateway / proxy).
/// </summary>
public static class GeminiServiceBuilder
{
    /// <summary>
    /// Builds a <see cref="GeminiService"/> from the specified <paramref name="options"/> using an internal <see cref="HttpClient"/>.
    /// Disposing the returned <see cref="GeminiService"/> disposes the internal <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="options">Endpoint and authentication header settings.</param>
    /// <returns>A configured <see cref="GeminiService"/> (which also implements <see cref="Microsoft.Extensions.AI.IChatClient"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    public static GeminiService Build(GenerativeLanguageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GeminiService(new GeminiClient(options), options.EndpointBase, options.JsonSerializerOptions);
    }

    /// <summary>
    /// Builds a <see cref="GeminiService"/> that sends requests through the supplied <paramref name="httpClient"/>
    /// (for example from <c>IHttpClientFactory</c> or a custom <see cref="HttpMessageHandler"/> pipeline).
    /// <para>
    /// The caller retains ownership of <paramref name="httpClient"/>; disposing the returned <see cref="GeminiService"/>
    /// will not dispose <paramref name="httpClient"/>, and <see cref="HttpClient.DefaultRequestHeaders"/> is never modified.
    /// </para>
    /// </summary>
    /// <param name="options">Endpoint and authentication header settings.</param>
    /// <param name="httpClient">The <see cref="HttpClient"/> to use for outgoing requests.</param>
    /// <returns>A configured <see cref="GeminiService"/> (which also implements <see cref="Microsoft.Extensions.AI.IChatClient"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="httpClient"/> is <c>null</c>.</exception>
    public static GeminiService Build(GenerativeLanguageOptions options, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        return new GeminiService(new GeminiClient(options, httpClient), options.EndpointBase, options.JsonSerializerOptions);
    }
}
