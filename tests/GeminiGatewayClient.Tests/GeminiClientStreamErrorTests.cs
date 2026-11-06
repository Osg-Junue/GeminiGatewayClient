using System.Net;

namespace GeminiGatewayClient.Tests;

public class GeminiClientStreamErrorTests
{
    private const string DefaultModel = "gemini-2.5-flash";

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
