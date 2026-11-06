using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GeminiGatewayClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

// Runs the public surface as a Native AOT binary, because reflection failures only appear at run time there.

var handler = new StubHandler();
var client = new GeminiClient(
    new GeminiClientOptions
    {
        ApiKeyHeader = "Ocp-Apim-Subscription-Key",
        ApiKeyProvider = _ => ValueTask.FromResult("rotated-key"),
        Model = "gemini-2.5-flash",
        EmbeddingModel = "gemini-embedding-001"
    },
    new HttpClient(handler));
var toolOptions = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine(SmokeJsonContext.Default, AIJsonUtilities.DefaultOptions.TypeInfoResolver) };
var chat = client.AsIChatClient(toolSerializerOptions: toolOptions);

var response = await chat.GetResponseAsync("hi");
Require(response.Text == "hello", "non-streaming text");
Require(response.Usage?.TotalTokenCount == 2, "usage");
Require(response.FinishReason == ChatFinishReason.Stop, "finish reason");
Require(handler.LastApiKey == "rotated-key", "custom API key header");
Require(response.Messages[0].Contents[0].Annotations?.Count == 1, "citation");

var streamed = await chat.GetStreamingResponseAsync("hi").ToChatResponseAsync();
Require(streamed.Text == "hello", "streaming text");

await chat.GetResponseAsync(
[
    new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "lookup")]),
    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", new ToolResult("sunny"))])
]);
Require(handler.LastBody?.Contains("sunny", StringComparison.Ordinal) == true, "source-generated tool result");

// Values with no metadata in any resolver, as a hand-written tool loop produces them.
var plainChat = client.AsIChatClient(toolSerializerOptions: AIJsonUtilities.DefaultOptions);
object?[] toolValues =
[
    new Dictionary<string, object?> { ["items"] = new List<int> { 1 }, ["id"] = Guid.Empty, ["counts"] = new Dictionary<string, int> { ["a"] = 1 } },
    Guid.Empty,
    new DateOnly(2026, 1, 2),
    new TimeOnly(1, 2, 3),
    new byte[] { 1 },
    new[] { "a", "b" }
];
foreach (var value in toolValues)
{
    await plainChat.GetResponseAsync(
    [
        new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "lookup")]),
        new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", value)])
    ]);
}

try
{
    await chat.GetResponseAsync("fail");
    Require(false, "error status");
}
catch (GeminiApiException error)
{
    Require(error.StatusCode == HttpStatusCode.TooManyRequests && error.RetryAfter == TimeSpan.FromSeconds(2), "error details");
}

var embedding = await client.AsIEmbeddingGenerator().GenerateAsync(["a"]);
Require(embedding[0].Vector.Length == 2, "embedding");

var services = new ServiceCollection();
services.AddGeminiClient(options =>
{
    options.ApiKey = "test-key";
    options.Model = "gemini-2.5-flash";
}).ConfigurePrimaryHttpMessageHandler(() => new StubHandler());
await using var provider = services.BuildServiceProvider();
var resolved = await provider.GetRequiredService<IChatClient>().GetResponseAsync("hi");
Require(resolved.Text == "hello", "dependency injection");

Console.WriteLine("AOT smoke test passed.");

static void Require(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"AOT smoke test failed: {name}");
    }
}

internal sealed record ToolResult(string Weather);

[JsonSerializable(typeof(ToolResult))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;

internal sealed class StubHandler : HttpMessageHandler
{
    private const string GenerateBody = """{"candidates":[{"content":{"role":"model","parts":[{"text":"hello"}]},"finishReason":"STOP","groundingMetadata":{"groundingChunks":[{"web":{"uri":"https://example.com"}}],"groundingSupports":[{"segment":{"endIndex":5},"groundingChunkIndices":[0]}]}}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}""";
    private const string ErrorBody = """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","details":[{"retryDelay":"2s"}]}}""";
    private const string StreamBody = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hel\"}]}}]}\n\ndata: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"lo\"}]},\"finishReason\":\"STOP\"}]}\n\n";
    private const string EmbeddingsBody = """{"embeddings":[{"values":[0.5,0.25]}]}""";

    public string? LastBody { get; private set; }

    public string? LastApiKey { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        LastApiKey = request.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var keys) ? keys.Single() : null;
        if (LastBody?.Contains("fail", StringComparison.Ordinal) == true)
        {
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(ErrorBody) };
        }

        var path = request.RequestUri!.AbsolutePath;
        var (body, mediaType) = path.Contains("streamGenerateContent", StringComparison.Ordinal) ? (StreamBody, "text/event-stream")
            : path.Contains("mbed", StringComparison.Ordinal) ? (EmbeddingsBody, "application/json")
            : (GenerateBody, "application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue(mediaType)) };
    }
}
