namespace GeminiGatewayClient.Tests;

/// <summary>
/// A header that cannot be sent is an exception. Line breaks are the one thing checked here, because .NET would send them as they are.
/// </summary>
public class GatewayHeaderValidationTests
{
    private const string OkResponse = """{"candidates":[]}""";

    [Theory]
    [InlineData("key\r\ninjected: 1")]
    [InlineData("key\n")]
    [InlineData("ke\ry")]
    [InlineData("k\0ey")]
    public void StaticKey_ThatWouldBreakTheHeader_IsRejectedWithoutShowingTheKey(string key)
    {
        var error = Assert.Throws<ArgumentException>(() => new GeminiClient(new GeminiClientOptions { ApiKey = key, ApiKeyHeader = "x-key", Model = "m" }));

        Assert.Contains("x-key", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("injected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prefix_ThatWouldBreakTheHeader_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new GeminiClient(new GeminiClientOptions { ApiKey = "k", ApiKeyPrefix = "Bearer\r\n", Model = "m" }));
    }

    [Fact]
    public void AdditionalHeader_ThatWouldBreakTheHeader_IsRejected()
    {
        var options = new GeminiClientOptions { Model = "m" };
        options.AdditionalHeaders["x-a"] = "b\r\ninjected: 1";

        Assert.Throws<ArgumentException>(() => new GeminiClient(options));
    }

    [Fact]
    public async Task KeyFromTheProvider_ThatWouldBreakTheHeader_FailsBeforeAnythingIsSent()
    {
        using var handler = new RecordingHandler(OkResponse);
        using var client = new GeminiClient(
            new GeminiClientOptions { ApiKeyProvider = _ => ValueTask.FromResult("key\r\ninjected: 1"), Model = "m" },
            new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateContentAsync("hi"));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Content-Length")]
    [InlineData("x key")]
    public void HeaderName_ThatDotNetRefuses_IsRejected(string name)
    {
        var options = new GeminiClientOptions { ApiKey = "k", ApiKeyHeader = name, Model = "m" };

        Assert.Throws<ArgumentException>(() => new GeminiClient(options));
    }

    [Theory]
    [InlineData("x-goog-api-key")]
    [InlineData("X-Goog-Api-Key")]
    public void SameHeaderAsTheKey_IsRejected(string name)
    {
        var options = new GeminiClientOptions { ApiKey = "a", Model = "m" };
        options.AdditionalHeaders[name] = "b";

        Assert.Throws<ArgumentException>(() => new GeminiClient(options));
    }

    [Fact]
    public void SameHeaderAsTheKeyFromTheProvider_IsRejectedWhenTheClientIsCreated()
    {
        var options = new GeminiClientOptions { ApiKeyProvider = _ => ValueTask.FromResult("a"), Model = "m" };
        options.AdditionalHeaders["x-goog-api-key"] = "b";

        Assert.Throws<ArgumentException>(() => new GeminiClient(options));
    }

    [Theory]
    [InlineData("Content-Type", null)]
    [InlineData("bad header", null)]
    [InlineData("x-key", "Bearer\r\ninjected: ")]
    public void ProviderHeader_ThatCannotBeSent_FailsWhenTheClientIsCreated(string name, string? prefix)
    {
        var options = new GeminiClientOptions { ApiKeyProvider = _ => ValueTask.FromResult("a"), ApiKeyHeader = name, ApiKeyPrefix = prefix, Model = "m" };

        Assert.Throws<ArgumentException>(() => new GeminiClient(options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmptyKeyFromTheProvider_FailsBeforeAnythingIsSent(string? key)
    {
        using var handler = new RecordingHandler(OkResponse);
        using var client = new GeminiClient(
            new GeminiClientOptions { ApiKeyProvider = _ => ValueTask.FromResult(key!), Model = "m" },
            new HttpClient(handler));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GenerateContentAsync("hi"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AcceptHeaderOnAStream_CanComeFromAdditionalHeaders()
    {
        using var handler = new RecordingHandler("data: " + OkResponse + "\n\n", contentType: "text/event-stream");
        var options = new GeminiClientOptions { Model = "m" };
        options.AdditionalHeaders["Accept"] = "application/json";
        using var client = new GeminiClient(options, new HttpClient(handler));

        await foreach (var _ in client.StreamGenerateContentAsync("hi"))
        {
        }

        Assert.Contains("text/event-stream", handler.LastRequest!.Headers.Accept.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ApiKeyAndProviderTogether_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new GeminiClient(new GeminiClientOptions
        {
            ApiKey = "k",
            ApiKeyProvider = _ => ValueTask.FromResult("k"),
            Model = "m"
        }));
    }
}
