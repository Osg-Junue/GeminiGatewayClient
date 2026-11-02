using GeminiGatewayClient;
using GeminiGatewayClient.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// <see cref="IServiceCollection"/> extension methods for registering <see cref="GeminiClient"/> (as <see cref="IGeminiClient"/>)
/// and <see cref="IChatClient"/> backed by <see cref="IHttpClientFactory"/>.
/// </summary>
public static class GeminiServiceCollectionExtensions
{
    /// <summary>
    /// The named <see cref="HttpClient"/> registration used by <see cref="AddGeminiClient(IServiceCollection, Action{GeminiClientOptions}?)"/>.
    /// </summary>
    public const string HttpClientName = "GeminiGatewayClient";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers <see cref="GeminiClient"/>, <see cref="IGeminiClient"/>, and <see cref="IChatClient"/> backed by <see cref="IHttpClientFactory"/>.
        /// <para>
        /// Options come from the options pattern, so <paramref name="configure"/> can be combined with
        /// <c>services.AddOptions&lt;GeminiClientOptions&gt;().BindConfiguration("Gemini")</c>. Options are validated when the client is first resolved.
        /// <see cref="GeminiClientOptions.Timeout"/> is applied to the named <see cref="HttpClient"/>, and the default primary handler does not
        /// follow redirects (so credential headers are never forwarded to another host).
        /// </para>
        /// <para>
        /// The returned <see cref="IHttpClientBuilder"/> can be used to attach resilience pipelines (for example <c>.AddStandardResilienceHandler()</c>),
        /// custom <see cref="DelegatingHandler"/> instances, or a different primary handler.
        /// </para>
        /// </summary>
        /// <param name="configure">Optional delegate that configures <see cref="GeminiClientOptions"/>.</param>
        /// <returns>An <see cref="IHttpClientBuilder"/> for further configuring the underlying <see cref="HttpClient"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
        public IHttpClientBuilder AddGeminiClient(Action<GeminiClientOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var optionsBuilder = services.AddOptions<GeminiClientOptions>();
            if (configure is not null)
            {
                optionsBuilder.Configure(configure);
            }

            return RegisterCore(services);
        }

        /// <summary>
        /// Registers <see cref="GeminiClient"/>, <see cref="IGeminiClient"/>, and <see cref="IChatClient"/> backed by <see cref="IHttpClientFactory"/>,
        /// configuring <see cref="GeminiClientOptions"/> using the <see cref="IServiceProvider"/>.
        /// </summary>
        /// <param name="configure">Delegate that configures <see cref="GeminiClientOptions"/> with access to <see cref="IServiceProvider"/>.</param>
        /// <returns>An <see cref="IHttpClientBuilder"/> for further configuring the underlying <see cref="HttpClient"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is <c>null</c>.</exception>
        public IHttpClientBuilder AddGeminiClient(Action<IServiceProvider, GeminiClientOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.AddOptions<GeminiClientOptions>()
                .Configure<IServiceProvider>((options, sp) => configure(sp, options));

            return RegisterCore(services);
        }
    }

    private static IHttpClientBuilder RegisterCore(IServiceCollection services)
    {
        var alreadyRegistered = services.Any(static d => d.ServiceType == typeof(GeminiClientSettings));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<GeminiClientOptions>, GeminiClientOptionsValidator>());

        services.TryAddSingleton(static sp =>
            GeminiClientSettings.Create(sp.GetRequiredService<IOptions<GeminiClientOptions>>().Value));

        var httpClientBuilder = services.AddHttpClient(HttpClientName);
        if (!alreadyRegistered)
        {
            httpClientBuilder
                .ConfigureHttpClient(static (sp, httpClient) =>
                    httpClient.Timeout = sp.GetRequiredService<GeminiClientSettings>().Timeout)
                .ConfigurePrimaryHttpMessageHandler(static () => GeminiClient.CreateDefaultHandler());
        }

        services.TryAddTransient(static sp => new GeminiClient(
            sp.GetRequiredService<GeminiClientSettings>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            disposeHttpClient: false));
        services.TryAddTransient<IGeminiClient>(static sp => sp.GetRequiredService<GeminiClient>());
        services.TryAddTransient<IChatClient>(static sp =>
            sp.GetRequiredService<IGeminiClient>().AsIChatClient(
                toolSerializerOptions: sp.GetRequiredService<GeminiClientSettings>().ToolSerializerOptions));

        return httpClientBuilder;
    }

    /// <summary>
    /// Surfaces <see cref="GeminiClientOptions"/> validation errors through the options pattern
    /// (<see cref="OptionsValidationException"/>) using the same rules as the <see cref="GeminiClient"/> constructor.
    /// </summary>
    private sealed class GeminiClientOptionsValidator : IValidateOptions<GeminiClientOptions>
    {
        public ValidateOptionsResult Validate(string? name, GeminiClientOptions options)
        {
            if (!string.IsNullOrEmpty(name))
            {
                return ValidateOptionsResult.Skip;
            }

            try
            {
                GeminiClientSettings.Create(options);
                return ValidateOptionsResult.Success;
            }
            catch (ArgumentException ex)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }
        }
    }
}
