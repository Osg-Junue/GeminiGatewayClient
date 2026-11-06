using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// Behavior of <see cref="IChatClient"/> as seen through the public API, checked on the wire with a stub handler.
/// </summary>
public class ChatBehaviorTests
{
    private const string OkResponse = """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""";

    private static GeminiClient CreateClient(RecordingHandler handler, JsonSerializerOptions? toolOptions = null) =>
        new(new GeminiClientOptions { ApiKey = "test-key", Model = "gemini-2.5-flash", ToolSerializerOptions = toolOptions }, new HttpClient(handler));

    private static async Task<JsonElement> SentParts(IEnumerable<ChatMessage> messages, int contentIndex, IChatClient? chat = null, RecordingHandler? handler = null)
    {
        handler ??= new RecordingHandler(OkResponse);
        chat ??= CreateClient(handler).AsIChatClient();
        await chat.GetResponseAsync(messages);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        return document.RootElement.GetProperty("contents")[contentIndex].GetProperty("parts").Clone();
    }

    [Fact]
    public async Task FunctionCall_FollowedByStopChunk_ReportsToolCallsLikeNonStreaming()
    {
        const string call = """{"functionCall":{"name":"f","args":{}}}""";
        var sse = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + call + "]}}]}\n\n" +
                  "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"\"}]},\"finishReason\":\"STOP\"}]}\n\n";
        var single = "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + call + "]},\"finishReason\":\"STOP\"}]}";

        using var streamHandler = new RecordingHandler(sse, contentType: "text/event-stream");
        var streamed = await CreateClient(streamHandler).AsIChatClient().GetStreamingResponseAsync("hi").ToChatResponseAsync();
        using var singleHandler = new RecordingHandler(single);
        var buffered = await CreateClient(singleHandler).AsIChatClient().GetResponseAsync("hi");

        Assert.Equal(ChatFinishReason.ToolCalls, buffered.FinishReason);
        Assert.Equal(buffered.FinishReason, streamed.FinishReason);
    }

    [Fact]
    public async Task StandaloneSignaturePart_StaysSeparateFromTheTextBeforeIt()
    {
        const string response = """{"candidates":[{"content":{"role":"model","parts":[{"text":"answer"},{"text":"","thoughtSignature":"U0lH"}]},"finishReason":"STOP"}]}""";
        using var handler = new RecordingHandler(response);
        var first = await CreateClient(handler).AsIChatClient().GetResponseAsync("hi");

        var parts = await SentParts([.. first.Messages, new ChatMessage(ChatRole.User, "next")], contentIndex: 0);

        Assert.Equal(2, parts.GetArrayLength());
        Assert.False(parts[0].TryGetProperty("thoughtSignature", out _));
        Assert.Equal("U0lH", parts[1].GetProperty("thoughtSignature").GetString());
    }

    [Theory]
    [InlineData("""{"inlineData":{"mimeType":"not a media type","data":"AAEC"}}""")]
    [InlineData("""{"fileData":{"mimeType":"bad","fileUri":"https://example.com/a"}}""")]
    public async Task PartWithUnparsableMediaType_DoesNotFailTheResponse(string badPart)
    {
        var response = "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hello\"}," + badPart + "]},\"finishReason\":\"STOP\"}]}";
        using var handler = new RecordingHandler(response);

        var result = await CreateClient(handler).AsIChatClient().GetResponseAsync("hi");

        Assert.Equal("hello", result.Text);
        Assert.Equal(2, result.Messages[0].Contents.Count);
    }

    [Fact]
    public async Task MalformedFunctionCall_DoesNotClaimToolCalls()
    {
        using var handler = new RecordingHandler("""{"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"MALFORMED_FUNCTION_CALL"}]}""");

        var result = await CreateClient(handler).AsIChatClient().GetResponseAsync("hi");

        Assert.NotEqual(ChatFinishReason.ToolCalls, result.FinishReason);
    }

    [Theory]
    [InlineData("MALFORMED_FUNCTION_CALL")]
    [InlineData("UNEXPECTED_TOOL_CALL")]
    [InlineData("TOO_MANY_TOOL_CALLS")]
    public async Task FailedToolCall_WithFunctionCallParts_DoesNotClaimToolCalls(string finishReason)
    {
        var response = "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"f\",\"args\":{}}}]},\"finishReason\":\"" + finishReason + "\"}]}";
        using var singleHandler = new RecordingHandler(response);
        using var streamHandler = new RecordingHandler("data: " + response + "\n\n", contentType: "text/event-stream");

        var buffered = await CreateClient(singleHandler).AsIChatClient().GetResponseAsync("hi");
        var updates = await CreateClient(streamHandler).AsIChatClient().GetStreamingResponseAsync("hi").ToListAsync();

        Assert.Null(buffered.FinishReason);
        Assert.Single(buffered.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.All(updates, update => Assert.Null(update.FinishReason));
        Assert.Null(updates.ToChatResponse().FinishReason);
    }

    [Fact]
    public async Task ResultWhoseSynthesizedCallWasTrimmed_KeepsTheFunctionName()
    {
        var callId = $"get_weather_call_{Guid.NewGuid():N}";

        var parts = await SentParts([new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, "sunny")])], contentIndex: 0);

        Assert.Equal("get_weather", parts[0].GetProperty("functionResponse").GetProperty("name").GetString());
    }

    [Fact]
    public async Task HostedFileContent_FailsInsteadOfBeingDropped()
    {
        using var handler = new RecordingHandler(OkResponse);
        var chat = CreateClient(handler).AsIChatClient();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            chat.GetResponseAsync([new ChatMessage(ChatRole.User, [new TextContent("read"), new HostedFileContent("files/abc")])]));
    }

    [Fact]
    public async Task ToolResults_UseTheCallersConverters_InsideDictionariesToo()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new MarkerDateConverter());
        using var handler = new RecordingHandler(OkResponse);
        var result = new Dictionary<string, object?> { ["t"] = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) };

        var parts = await SentParts(
            [
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "f")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", result)])
            ],
            contentIndex: 1,
            CreateClient(handler, options).AsIChatClient(),
            handler);

        Assert.Equal("MARKER", parts[0].GetProperty("functionResponse").GetProperty("response").GetProperty("t").GetString());
    }

    [Fact]
    public async Task DecoratedClient_ForwardingToolOptions_KeepsTheCallersOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new MarkerDateConverter());
        using var handler = new RecordingHandler(OkResponse);
        IGeminiClient decorated = new ForwardingClient(CreateClient(handler, options));

        var parts = await SentParts(
            [
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "f")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new Dictionary<string, object?> { ["t"] = DateTime.UnixEpoch })])
            ],
            contentIndex: 1,
            decorated.AsIChatClient(),
            handler);

        Assert.Equal("MARKER", parts[0].GetProperty("functionResponse").GetProperty("response").GetProperty("t").GetString());
    }

    [Fact]
    public async Task StreamWithOnlyKeepAliveComments_IsReportedAsEmpty()
    {
        using var handler = new RecordingHandler(": keep-alive\n\n: keep-alive\n\n", contentType: "text/event-stream");
        var chat = CreateClient(handler).AsIChatClient();

        await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in chat.GetStreamingResponseAsync("hi"))
            {
            }
        });
    }

    private sealed class MarkerDateConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue("MARKER");
    }

    private sealed class ForwardingClient(IGeminiClient inner) : IGeminiClient
    {
        public Uri Endpoint => inner.Endpoint;

        public string? DefaultModel => inner.DefaultModel;

        public JsonSerializerOptions? ToolSerializerOptions => inner.ToolSerializerOptions;

        public Task<GenerateContentResponse> GenerateContentAsync(GenerateContentRequest request, string? model = null, CancellationToken cancellationToken = default) =>
            inner.GenerateContentAsync(request, model, cancellationToken);

        public IAsyncEnumerable<GenerateContentResponse> StreamGenerateContentAsync(GenerateContentRequest request, string? model = null, CancellationToken cancellationToken = default) =>
            inner.StreamGenerateContentAsync(request, model, cancellationToken);

        public Task<EmbedContentResponse> EmbedContentAsync(EmbedContentRequest request, string? model = null, CancellationToken cancellationToken = default) =>
            inner.EmbedContentAsync(request, model, cancellationToken);

        public Task<BatchEmbedContentsResponse> BatchEmbedContentsAsync(BatchEmbedContentsRequest request, string? model = null, CancellationToken cancellationToken = default) =>
            inner.BatchEmbedContentsAsync(request, model, cancellationToken);
    }
}
