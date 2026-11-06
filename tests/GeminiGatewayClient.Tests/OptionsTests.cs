using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GeminiGatewayClient.Tests;

public class OptionsTests
{
    private const string DefaultModel = "gemini-2.5-flash";

    [Fact]
    public void GeminiClientOptions_ToString_RedactsApiKeyAndAdditionalHeaderValues()
    {
        var options = new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "AIzaSySuperSecretKeyValue12345",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["cf-aig-authorization"] = "Bearer SecretCloudflareToken999"
            }
        };

        var text = options.ToString();

        Assert.DoesNotContain("AIzaSySuperSecretKeyValue12345", text);
        Assert.DoesNotContain("SecretCloudflareToken999", text);
        Assert.Contains("ApiKey = ***", text);
        Assert.Contains("cf-aig-authorization", text);
    }

    [Fact]
    public void GeminiClientOptions_SettingBothApiKeyAndApiKeyProvider_ThrowsArgumentException()
    {
        var options = new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "static-key",
            ApiKeyProvider = _ => ValueTask.FromResult("dynamic-key")
        };

        Assert.Throws<ArgumentException>(() => new GeminiClient(options));
    }

    [Fact]
    public void AdditionalHeaders_ContainingContentHeader_ThrowsArgumentExceptionInConstructor()
    {
        var options = new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain"
            }
        };

        Assert.Throws<ArgumentException>(() => new GeminiClient(options));
    }

    [Fact]
    public void AdditionalHeaders_WithCrLfInjection_ThrowsArgumentExceptionInConstructor()
    {
        var options = new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["x-custom"] = "val\r\nInjected: bad"
            }
        };

        Assert.Throws<ArgumentException>(() => new GeminiClient(options));
    }

    [Fact]
    public void PlainHttpEndpoint_RejectedForRemoteHost_AllowedForLoopbackOrWhenExplicitlyEnabled()
    {
        Assert.Throws<ArgumentException>(() => new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("http://example.com/v1beta"),
            Model = DefaultModel,
            ApiKey = "key"
        }));

        using var loopbackClient = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("http://localhost:8080/v1beta"),
            Model = DefaultModel,
            ApiKey = "key"
        });

        using var explicitInsecureClient = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("http://internal-proxy.corp/v1beta"),
            Model = DefaultModel,
            ApiKey = "key",
            AllowInsecureHttp = true
        });
    }

    [Fact]
    public async Task Endpoint_WithQueryParameters_PreservesQueryAfterAppendingModelAndOperation()
    {
        const string okJson = """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"ok"}]}}]}""";
        using var handler = new RecordingHandler(okJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta?api-version=2026-01-01"),
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        await client.GenerateContentAsync("Hi");
        Assert.Equal(
            "https://contoso.azure-api.net/gemini/v1beta/models/gemini-2.5-flash:generateContent?api-version=2026-01-01",
            handler.LastRequest?.RequestUri?.ToString());

        await foreach (var _ in client.StreamGenerateContentAsync("Hi"))
        {
        }

        Assert.Equal(
            "https://contoso.azure-api.net/gemini/v1beta/models/gemini-2.5-flash:streamGenerateContent?api-version=2026-01-01&alt=sse",
            handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public void InternalHttpClient_UsesDefaultFiveMinuteTimeout()
    {
        using var defaultClient = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        });
        Assert.Equal(TimeSpan.FromMinutes(5), defaultClient.HttpClientTimeout);
    }

    [Fact]
    public async Task AddGeminiClient_RegistersGeminiClientIGeminiClientAndIChatClientWithOptions()
    {
        const string okJson = """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"From DI!"}]}}]}""";
        using var handler = new RecordingHandler(okJson);

        var services = new ServiceCollection();
        services.AddGeminiClient(options =>
        {
            options.Model = DefaultModel;
            options.ApiKey = "di-key";
            options.ApiKeyHeader = "Ocp-Apim-Subscription-Key";
        }).ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();
        var geminiClient = provider.GetRequiredService<GeminiClient>();
        var iGeminiClient = provider.GetRequiredService<IGeminiClient>();
        var chatClient = provider.GetRequiredService<IChatClient>();

        Assert.Equal(TimeSpan.FromMinutes(5), geminiClient.HttpClientTimeout);

        var directResponse = await iGeminiClient.GenerateContentAsync("Hi");
        Assert.Equal("From DI!", directResponse.Text);

        var chatResponse = await chatClient.GetResponseAsync("Hi via IChatClient");
        Assert.Equal("From DI!", chatResponse.Text);
        Assert.Equal("di-key", Assert.Single(handler.LastRequest!.Headers.GetValues("Ocp-Apim-Subscription-Key")));
    }

    [Fact]
    public void AddGeminiClient_InvalidOptions_ThrowsOptionsValidationException()
    {
        var services = new ServiceCollection();
        services.AddGeminiClient(options =>
        {
            options.Endpoint = new Uri("http://remote-insecure-host.example.com/v1beta");
            options.Model = DefaultModel;
        });

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IGeminiClient>());
    }

    [Fact]
    public async Task AddGeminiClient_WithServiceProviderOverload_ResolvesConfigurationFromContainer()
    {
        const string okJson = """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"From SP!"}]}}]}""";
        using var handler = new RecordingHandler(okJson);

        var services = new ServiceCollection();
        services.AddSingleton("resolved-secret-key");
        services.AddGeminiClient((sp, options) =>
        {
            options.Model = DefaultModel;
            options.ApiKey = sp.GetRequiredService<string>();
        }).ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IGeminiClient>();
        var response = await client.GenerateContentAsync("Hi");

        Assert.Equal("From SP!", response.Text);
        Assert.Equal("resolved-secret-key", Assert.Single(handler.LastRequest!.Headers.GetValues("x-goog-api-key")));
    }

    [Fact]
    public void AddGeminiClient_CalledMultipleTimes_DoesNotDuplicateConfigureHttpClientActions_AndSkipsUnrelatedNamedOptions()
    {
        var services = new ServiceCollection();
        services.AddGeminiClient(options =>
        {
            options.Model = DefaultModel;
            options.ApiKey = "first-key";
        });
        services.AddGeminiClient(options =>
        {
        });

        // Unrelated named GeminiClientOptions instance should not trigger default validator failure
        services.AddOptions<GeminiClientOptions>("unrelated-named")
            .Configure(o => o.Endpoint = new Uri("http://remote-insecure-host.example.com/v1beta"));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<GeminiClient>();
        Assert.Equal(TimeSpan.FromMinutes(5), client.HttpClientTimeout);

        var named = provider.GetRequiredService<IOptionsSnapshot<GeminiClientOptions>>().Get("unrelated-named");
        Assert.Equal(new Uri("http://remote-insecure-host.example.com/v1beta"), named.Endpoint);
    }

    [Fact]
    public void GeminiClientOptions_BindConfiguration_BindsAllPropertiesIncludingAdditionalHeaders()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gemini:Endpoint"] = "https://gateway.example.com/gemini/v1beta",
                ["Gemini:Model"] = "gemini-2.5-pro",
                ["Gemini:EmbeddingModel"] = "gemini-embedding-001",
                ["Gemini:ApiKey"] = "cfg-secret-key",
                ["Gemini:ApiKeyHeader"] = "Authorization",
                ["Gemini:ApiKeyPrefix"] = "Bearer ",
                ["Gemini:AdditionalHeaders:X-Tenant-Id"] = "tenant-42",
                ["Gemini:AdditionalHeaders:X-Environment"] = "production",
                ["Gemini:StreamIdleTimeout"] = "00:01:15",
                ["Gemini:AllowInsecureHttp"] = "true"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<GeminiClientOptions>().BindConfiguration("Gemini");
        services.AddGeminiClient();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();

        var bound = provider.GetRequiredService<IOptions<GeminiClientOptions>>().Value;
        Assert.Equal(new Uri("https://gateway.example.com/gemini/v1beta"), bound.Endpoint);
        Assert.Equal("gemini-2.5-pro", bound.Model);
        Assert.Equal("gemini-embedding-001", bound.EmbeddingModel);
        Assert.Equal("cfg-secret-key", bound.ApiKey);
        Assert.Equal("Authorization", bound.ApiKeyHeader);
        Assert.Equal("Bearer ", bound.ApiKeyPrefix);
        Assert.Equal("tenant-42", bound.AdditionalHeaders["X-Tenant-Id"]);
        Assert.Equal("production", bound.AdditionalHeaders["X-Environment"]);
        Assert.Equal(TimeSpan.FromSeconds(75), bound.StreamIdleTimeout);
        Assert.True(bound.AllowInsecureHttp);
    }

    [Fact]
    public void AddGeminiClient_TimeoutCanBeChangedOnTheBuilder()
    {
        var services = new ServiceCollection();
        services.AddGeminiClient(options => options.Model = DefaultModel)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(9));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(9), provider.GetRequiredService<GeminiClient>().HttpClientTimeout);
    }

    [Fact]
    public void GeminiClientOptions_DefaultStreamIdleTimeout_IsTwoMinutes()
    {
        var options = new GeminiClientOptions();
        Assert.Equal(TimeSpan.FromMinutes(2), options.StreamIdleTimeout);
    }
}
