using GeminiAPIWrapper.DependencyInjection;
using GeminiAPIWrapper.Extensions;
using GeminiAPIWrapper.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GeminiAPIWrapper.Tests;

public class OptionsTests
{
    private const string SampleEndpoint = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash";

    [Fact]
    public void GenerativeLanguageOptions_ToString_RedactsApiKeyAndAdditionalHeaderValues()
    {
        var options = new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
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
    public void AdditionalHeaders_ContainingContentHeader_ThrowsArgumentExceptionInConstructor()
    {
        var options = new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Content-Type"] = "text/plain"
            }
        };

        Assert.Throws<ArgumentException>(() => GeminiServiceBuilder.Build(options));
    }

    [Fact]
    public void AdditionalHeaders_DuplicatingKeyHeaderCaseInsensitive_ThrowsArgumentExceptionInConstructor()
    {
        var options = new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key",
            KeyHeader = "x-goog-api-key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["X-GOOG-API-KEY"] = "second-key"
            }
        };

        Assert.Throws<ArgumentException>(() => GeminiServiceBuilder.Build(options));
    }

    [Fact]
    public void AdditionalHeaders_WithCrLfInjection_ThrowsArgumentExceptionInConstructor()
    {
        var options = new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["x-custom"] = "val\r\nInjected: bad"
            }
        };

        Assert.Throws<ArgumentException>(() => GeminiServiceBuilder.Build(options));
    }

    [Fact]
    public void PlainHttpEndpoint_RejectedForRemoteHost_AllowedForLoopbackOrWhenExplicitlyEnabled()
    {
        Assert.Throws<ArgumentException>(() => GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = "http://example.com/v1beta/models/gemini-2.5-flash",
            ApiKey = "key"
        }));

        using var loopbackService = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = "http://localhost:8080/v1beta/models/gemini-2.5-flash",
            ApiKey = "key"
        });

        using var explicitInsecureService = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = "http://internal-proxy.corp/v1beta/models/gemini-2.5-flash",
            ApiKey = "key",
            AllowInsecureHttp = true
        });
    }

    [Fact]
    public async Task EndpointBase_WithQueryParameters_PreservesQueryAfterAppendingOperation()
    {
        const string okJson = """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"ok"}]}}]}""";
        using var handler = new GeminiClientTests.RecordingHandler(okJson, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = "https://contoso.azure-api.net/gemini/v1beta/models/gemini-2.5-flash?api-version=2026-01-01",
            ApiKey = "key"
        }, httpClient);

        await service.GenerateAsync("Hi");
        Assert.Equal(
            "https://contoso.azure-api.net/gemini/v1beta/models/gemini-2.5-flash:generateContent?api-version=2026-01-01",
            handler.LastRequest?.RequestUri?.ToString());

        await foreach (var _ in service.StreamGenerateAsync("Hi"))
        {
        }

        Assert.Equal(
            "https://contoso.azure-api.net/gemini/v1beta/models/gemini-2.5-flash:streamGenerateContent?api-version=2026-01-01&alt=sse",
            handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public void InternalHttpClient_UsesDefaultFiveMinuteTimeout_OrConfiguredTimeout()
    {
        using var defaultClient = new GeminiClient(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key"
        });
        Assert.Equal(TimeSpan.FromMinutes(5), defaultClient.HttpClientTimeout);

        using var customClient = new GeminiClient(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key",
            Timeout = TimeSpan.FromMinutes(10)
        });
        Assert.Equal(TimeSpan.FromMinutes(10), customClient.HttpClientTimeout);
    }

    [Fact]
    public async Task AddGeminiService_RegistersGeminiServiceAndIChatClientWithHttpClientFactory()
    {
        const string okJson = """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"From DI!"}]}}]}""";
        using var handler = new GeminiClientTests.RecordingHandler(okJson);

        var services = new ServiceCollection();
        services.AddGeminiService(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "di-key",
            KeyHeader = "Ocp-Apim-Subscription-Key"
        }).ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();
        var geminiService = provider.GetRequiredService<GeminiService>();
        var chatClient = provider.GetRequiredService<IChatClient>();

        var directResponse = await geminiService.GenerateAsync("Hi");
        Assert.Equal("From DI!", directResponse.GetText());

        var chatResponse = await chatClient.GetResponseAsync("Hi via IChatClient");
        Assert.Equal("From DI!", chatResponse.Text);
        Assert.Equal("di-key", Assert.Single(handler.LastRequest!.Headers.GetValues("Ocp-Apim-Subscription-Key")));
    }
}
