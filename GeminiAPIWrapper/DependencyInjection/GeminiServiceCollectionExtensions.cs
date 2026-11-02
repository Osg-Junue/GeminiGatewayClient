using GeminiAPIWrapper.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GeminiAPIWrapper.DependencyInjection;

/// <summary>
/// <see cref="IServiceCollection"/> extension methods for registering <see cref="GeminiService"/>
/// and <see cref="IChatClient"/> with <see cref="IHttpClientFactory"/>.
/// </summary>
public static class GeminiServiceCollectionExtensions
{
    /// <summary>
    /// The named <see cref="HttpClient"/> registration name used by <see cref="AddGeminiService(IServiceCollection, GenerativeLanguageOptions)"/>.
    /// </summary>
    public const string HttpClientName = nameof(GeminiService);

    /// <summary>
    /// Registers <see cref="GeminiService"/> (and <see cref="IChatClient"/>) backed by <see cref="IHttpClientFactory"/>
    /// using the specified <paramref name="options"/>.
    /// <para>
    /// The returned <see cref="IHttpClientBuilder"/> can be used to attach resilience pipelines
    /// (for example <c>.AddStandardResilienceHandler()</c>), custom <see cref="HttpMessageHandler"/> delegates, or timeouts.
    /// </para>
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="options">Endpoint and authentication header settings.</param>
    /// <returns>An <see cref="IHttpClientBuilder"/> for further configuring the underlying <see cref="HttpClient"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    public static IHttpClientBuilder AddGeminiService(
        this IServiceCollection services,
        GenerativeLanguageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // Validate options eagerly at registration time.
        using (new GeminiClient(options))
        {
        }

        services.TryAddSingleton(options);
        return RegisterCore(services);
    }

    /// <summary>
    /// Registers <see cref="GeminiService"/> (and <see cref="IChatClient"/>) backed by <see cref="IHttpClientFactory"/>
    /// using an options factory resolved from the <see cref="IServiceProvider"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="optionsFactory">Factory that creates <see cref="GenerativeLanguageOptions"/> from the service provider.</param>
    /// <returns>An <see cref="IHttpClientBuilder"/> for further configuring the underlying <see cref="HttpClient"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="optionsFactory"/> is <c>null</c>.</exception>
    public static IHttpClientBuilder AddGeminiService(
        this IServiceCollection services,
        Func<IServiceProvider, GenerativeLanguageOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.TryAddSingleton(optionsFactory);
        return RegisterCore(services);
    }

    private static IHttpClientBuilder RegisterCore(IServiceCollection services)
    {
        var builder = services.AddHttpClient(HttpClientName);

        services.TryAddTransient(sp =>
        {
            var options = sp.GetRequiredService<GenerativeLanguageOptions>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            return GeminiServiceBuilder.Build(options, httpClient);
        });

        services.TryAddTransient<IChatClient>(sp => sp.GetRequiredService<GeminiService>());
        return builder;
    }
}
