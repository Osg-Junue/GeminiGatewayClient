using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Checks against the real API that text sent as UTF-8 reaches the model unchanged.
/// </summary>
public sealed class RequestEncodingExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    private const string Prompt = "次の文字列を一切変更せず、そのまま 1 行で返してください: 東京タワー<b>&\"🌸\"";

    private static GeminiClient CreateClient(BodyRecordingHandler handler) => new(
        new GeminiClientOptions { ApiKey = ExploratoryTestSettings.GetRequiredApiKey(), Model = ExploratoryTestSettings.GetModel() },
        new HttpClient(handler));

    [ExploratoryFact]
    public async Task Generate_JapaneseSentAsUtf8_ReachesTheModelUnchanged()
    {
        LogSection("UTF-8 request body (generate)");
        using var handler = new BodyRecordingHandler();
        using var client = CreateClient(handler);

        var response = await client.GenerateContentAsync(Prompt);

        Output.WriteLine($"body bytes={handler.LastBody!.Length}, answer={response.Text}");
        Assert.Contains("東京タワー", System.Text.Encoding.UTF8.GetString(handler.LastBody), StringComparison.Ordinal);
        Assert.Contains("東京タワー<b>&\"🌸\"", response.Text, StringComparison.Ordinal);
    }

    [ExploratoryFact]
    public async Task ChatStream_JapaneseSentAsUtf8_ReachesTheModelUnchanged()
    {
        LogSection("UTF-8 request body (chat stream)");
        using var handler = new BodyRecordingHandler();
        using var client = CreateClient(handler);

        var response = await client.AsIChatClient().GetStreamingResponseAsync(Prompt).ToChatResponseAsync();

        Assert.Contains("東京タワー", System.Text.Encoding.UTF8.GetString(handler.LastBody!), StringComparison.Ordinal);
        Assert.Contains("東京タワー<b>&\"🌸\"", response.Text, StringComparison.Ordinal);
    }

    [ExploratoryFact]
    public async Task Embedding_JapaneseSentAsUtf8_IsAccepted()
    {
        LogSection("UTF-8 request body (embedding)");
        using var handler = new BodyRecordingHandler();
        using var client = new GeminiClient(
            new GeminiClientOptions { ApiKey = ExploratoryTestSettings.GetRequiredApiKey(), EmbeddingModel = "gemini-embedding-001" },
            new HttpClient(handler));

        var response = await client.EmbedContentAsync("東京タワー");

        Assert.Contains("東京タワー", System.Text.Encoding.UTF8.GetString(handler.LastBody!), StringComparison.Ordinal);
        Assert.NotEmpty(response.Embedding!.Values);
    }

    private sealed class BodyRecordingHandler() : DelegatingHandler(GeminiClient.CreateDefaultHandler())
    {
        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
