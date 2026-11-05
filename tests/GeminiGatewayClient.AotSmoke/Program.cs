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
var client = new GeminiClient(new GeminiClientOptions { ApiKey = "test-key", Model = "gemini-2.5-flash", EmbeddingModel = "gemini-embedding-001" }, new HttpClient(handler));
var toolOptions = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine(SmokeJsonContext.Default, AIJsonUtilities.DefaultOptions.TypeInfoResolver) };
var chat = client.AsIChatClient(toolSerializerOptions: toolOptions);

var response = await chat.GetResponseAsync("hi");
Require(response.Text == "hello", "non-streaming text");
Require(response.Usage?.TotalTokenCount == 2, "usage");

var streamed = await chat.GetStreamingResponseAsync("hi").ToChatResponseAsync();
Require(streamed.Text == "hello", "streaming text");

await chat.GetResponseAsync(
[
    new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "lookup")]),
    new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", new ToolResult("sunny"))])
]);
Require(handler.LastBody?.Contains("sunny", StringComparison.Ordinal) == true, "source-generated tool result");

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
    private const string GenerateBody = """{"candidates":[{"content":{"role":"model","parts":[{"text":"hello"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2}}""";
    private const string StreamBody = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hel\"}]}}]}\n\ndata: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"lo\"}]},\"finishReason\":\"STOP\"}]}\n\n";
    private const string EmbeddingsBody = """{"embedding":{"values":[0.5,0.25]},"embeddings":[{"values":[0.5,0.25]}]}""";

    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        var (body, mediaType) = path.Contains("streamGenerateContent", StringComparison.Ordinal) ? (StreamBody, "text/event-stream")
            : path.Contains("mbed", StringComparison.Ordinal) ? (EmbeddingsBody, "application/json")
            : (GenerateBody, "application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue(mediaType)) };
    }
}
