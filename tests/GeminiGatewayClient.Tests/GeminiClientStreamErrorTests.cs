using System.Net;

namespace GeminiGatewayClient.Tests;

public class GeminiClientStreamErrorTests
{
    private const string DefaultModel = "gemini-2.5-flash";

    private const string ErrorEvent =
        """{"error":{"code":503,"message":"The model is overloaded.","status":"UNAVAILABLE","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"5s"}]}}""";

    private const string PartialChunk =
        """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"partial"}]}}]}""";

    [Theory]
    [InlineData(GeminiStreamingFormat.ServerSentEvents)]
    [InlineData(GeminiStreamingFormat.JsonArray)]
    public async Task Stream_InStreamErrorPayload_ThrowsGeminiApiException(GeminiStreamingFormat format)
    {
        var payload = format == GeminiStreamingFormat.ServerSentEvents
            ? $"data: {PartialChunk}\n\ndata: {ErrorEvent}\n\n"
            : $"[{PartialChunk},{ErrorEvent}]";

        using var handler = new RecordingHandler(payload);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key",
            StreamingFormat = format
        }, httpClient);

        var received = new List<string?>();
        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var chunk in client.StreamGenerateContentAsync("Hi"))
            {
                received.Add(chunk.Text);
            }
        });

        Assert.Equal(["partial"], received);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.True(ex.IsTransient);
        Assert.Equal("UNAVAILABLE", ex.ErrorStatus);
        Assert.Equal("The model is overloaded.", ex.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
    }

    [Fact]
    public async Task Generate_NullJsonBody_ThrowsGeminiApiException()
    {
        using var handler = new RecordingHandler("null");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync("Hi"));
    }
}
