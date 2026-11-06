using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// The library's main promise: credentials reach a gateway in exactly the shape it expects, and nowhere else.
/// </summary>
public class GatewayCredentialTests
{
    private const string OkResponse = """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""";

    private static async Task<HttpRequestMessage> SendAsync(Action<GeminiClientOptions> configure)
    {
        var options = new GeminiClientOptions { Model = "gemini-2.5-flash" };
        configure(options);
        using var handler = new RecordingHandler(OkResponse);
        using var client = new GeminiClient(options, new HttpClient(handler));
        await client.GenerateContentAsync("hi");
        return handler.LastRequest!;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task DefaultHeader_CarriesTheKey()
    {
        var request = await SendAsync(options => options.ApiKey = "k");

        Assert.Equal("k", Header(request, GeminiClientOptions.DefaultApiKeyHeader));
    }

    [Fact]
    public async Task RenamedHeader_ReplacesTheDefaultOne()
    {
        var request = await SendAsync(options =>
        {
            options.ApiKey = "k";
            options.ApiKeyHeader = "Ocp-Apim-Subscription-Key";
        });

        Assert.Equal("k", Header(request, "Ocp-Apim-Subscription-Key"));
        Assert.Null(Header(request, GeminiClientOptions.DefaultApiKeyHeader));
    }

    [Fact]
    public async Task Prefix_PrecedesTheKey()
    {
        var request = await SendAsync(options =>
        {
            options.ApiKey = "k";
            options.ApiKeyHeader = "Authorization";
            options.ApiKeyPrefix = "Bearer ";
        });

        Assert.Equal("Bearer k", Header(request, "Authorization"));
    }

    [Fact]
    public async Task KeyProvider_IsAskedOnEveryRequest()
    {
        var calls = 0;
        var options = new GeminiClientOptions
        {
            Model = "gemini-2.5-flash",
            ApiKeyHeader = "x-gateway-key",
            ApiKeyProvider = _ => ValueTask.FromResult($"rotated-{++calls}")
        };
        using var handler = new RecordingHandler(OkResponse);
        using var client = new GeminiClient(options, new HttpClient(handler));

        await client.GenerateContentAsync("one");
        var first = Header(handler.LastRequest!, "x-gateway-key");
        await client.GenerateContentAsync("two");

        Assert.Equal("rotated-1", first);
        Assert.Equal("rotated-2", Header(handler.LastRequest!, "x-gateway-key"));
    }

    [Fact]
    public async Task NoKey_SendsNoCredentialHeader()
    {
        var request = await SendAsync(options => options.AdditionalHeaders["x-tenant"] = "a");

        Assert.Null(Header(request, GeminiClientOptions.DefaultApiKeyHeader));
        Assert.Equal("a", Header(request, "x-tenant"));
    }

    [Fact]
    public void DefaultHandler_DoesNotFollowRedirectsOrKeepCookies()
    {
        using var handler = GeminiClient.CreateDefaultHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public void DependencyInjection_UsesAHandlerThatDoesNotFollowRedirects()
    {
        var services = new ServiceCollection();
        services.AddGeminiClient(options =>
        {
            options.ApiKey = "k";
            options.Model = "gemini-2.5-flash";
        });
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(GeminiServiceCollectionExtensions.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public async Task Redirect_IsReportedInsteadOfBeingFollowed()
    {
        var handler = new RecordingHandler(string.Empty, HttpStatusCode.TemporaryRedirect, configureResponse: response => response.Headers.Location = new Uri("https://attacker.example/"));
        using var client = new GeminiClient(new GeminiClientOptions { ApiKey = "k", Model = "gemini-2.5-flash" }, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync("hi"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, error.StatusCode);
    }

    [Fact]
    public async Task RequestBody_HasAContentLengthForGatewaysThatRequireOne()
    {
        var request = await SendAsync(options => options.ApiKey = "k");

        Assert.NotNull(request.Content!.Headers.ContentLength);
    }

    [Fact]
    public void Description_HidesCredentialsInTheEndpoint()
    {
        var options = new GeminiClientOptions
        {
            Endpoint = new Uri("https://user:secret@gateway.example.com/gemini?key=hidden#frag"),
            ApiKey = "k",
            Model = "gemini-2.5-flash"
        };

        var text = options.ToString();

        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", text, StringComparison.Ordinal);
        Assert.Contains("gateway.example.com/gemini", text, StringComparison.Ordinal);
    }
}
