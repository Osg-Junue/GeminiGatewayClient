using System.Net;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Models;
using GeminiGatewayClient.Serialization;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Benchmarks;

/// <summary>
/// Per-turn costs that grow with conversation length, measured in isolation and end to end through
/// <see cref="IChatClient"/> (mapping, validation, JSON serialization, HTTP plumbing, response parsing).
/// Inputs are built once and never mutated; every invocation walks the whole history.
/// </summary>
[MemoryDiagnoser]
public class RequestPipelineBenchmarks
{
    private const string ModelId = "gemini-2.5-flash";

    [Params(1, 16, 64)]
    public int MessageCount { get; set; }

    private GeminiClient _client = null!;
    private IChatClient _chatClient = null!;
    private List<ChatMessage> _textHistory = null!;
    private List<ChatMessage> _toolHistory = null!;
    private GenerateContentRequest _textRequest = null!;
    private GenerateContentRequest _toolRequest = null!;
    private BatchEmbedContentsRequest _embedBatch = null!;

    [GlobalSetup]
    public void Setup()
    {
        var responseBody = Encoding.UTF8.GetBytes(
            """{"responseId":"r","modelVersion":"gemini-2.5-flash","candidates":[{"content":{"role":"model","parts":[{"text":"Done."}]},"finishReason":"STOP"}]}""");
        _client = new GeminiClient(
            new GeminiClientOptions { ApiKey = "test-api-key", Model = ModelId },
            new HttpClient(new CannedResponseHandler(() => new ByteArrayContent(responseBody))));
        _chatClient = _client.AsIChatClient();

        _textHistory = new List<ChatMessage>(MessageCount);
        for (var i = 0; i < MessageCount; i++)
        {
            _textHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Turn {i + 1}: Conversation turn payload with realistic text length for scaling benchmark."));
        }

        _toolHistory = new List<ChatMessage>(MessageCount + 2);
        for (var i = 0; _toolHistory.Count < MessageCount; i++)
        {
            _toolHistory.Add(new ChatMessage(ChatRole.User, $"Check the forecast for day {i}."));
            _toolHistory.Add(ReplayedToolCall(i));
            _toolHistory.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", $"{i % 30}C and sunny")]));
        }

        _toolHistory.RemoveRange(MessageCount, _toolHistory.Count - MessageCount);

        _textRequest = ChatRequestMapper.ToRequest(_textHistory, options: null, _chatClient, ModelId, AIJsonUtilities.DefaultOptions);
        _toolRequest = ChatRequestMapper.ToRequest(_toolHistory, options: null, _chatClient, ModelId, AIJsonUtilities.DefaultOptions);

        var embedRequests = new List<EmbedContentRequest>(MessageCount);
        for (var i = 0; i < MessageCount; i++)
        {
            embedRequests.Add(EmbedContentRequest.FromText($"Document {i} to embed."));
        }

        _embedBatch = new BatchEmbedContentsRequest { Requests = embedRequests };
    }

    /// <summary>
    /// Produces an assistant turn the same way production does: wire JSON through the source-generated deserializer and the mapper.
    /// </summary>
    private static ChatMessage ReplayedToolCall(int index)
    {
        var json = $$$$"""{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"id":"call-{{{{index}}}}","name":"get_forecast","args":{"city":"Tokyo","day":{{{{index}}}},"units":"celsius","detail":{"hourly":true}}},"thoughtSignature":"sig-{{{{index}}}}"}]}}]}""";
        var response = JsonSerializer.Deserialize(json, GeminiJsonContext.Default.GenerateContentResponse)!;
        return ChatResponseMapper.ToChatResponse(response, ModelId).Messages[0];
    }

    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    [Benchmark]
    public GenerateContentRequest ToRequest_TextHistory() =>
        ChatRequestMapper.ToRequest(_textHistory, options: null, _chatClient, ModelId, AIJsonUtilities.DefaultOptions);

    [Benchmark]
    public GenerateContentRequest ToRequest_ToolHistoryReplay() =>
        ChatRequestMapper.ToRequest(_toolHistory, options: null, _chatClient, ModelId, AIJsonUtilities.DefaultOptions);

    [Benchmark]
    public void Validate_TextHistory() => _textRequest.Validate();

    [Benchmark]
    public void Validate_ToolHistory() => _toolRequest.Validate();

    [Benchmark]
    public void Validate_EmbedBatch() => _embedBatch.Validate();

    [Benchmark]
    public Task<ChatResponse> EndToEnd_TextHistory() => _chatClient.GetResponseAsync(_textHistory);

    [Benchmark]
    public Task<ChatResponse> EndToEnd_ToolHistory() => _chatClient.GetResponseAsync(_toolHistory);
}

/// <summary>
/// Buffered <c>:generateContent</c> body reads, with and without a <c>Content-Length</c> header, across body sizes that do and
/// do not sit near <see cref="System.Buffers.ArrayPool{T}"/> bucket boundaries.
/// </summary>
[MemoryDiagnoser]
public class BufferedResponseBenchmarks
{
    [Params(1_000, 4_096, 100_000, 1_048_576)]
    public int BodyBytes { get; set; }

    private GeminiClient _knownLengthClient = null!;
    private GeminiClient _unknownLengthClient = null!;
    private GenerateContentRequest _request = null!;

    [GlobalSetup]
    public void Setup()
    {
        const string prefix = """{"candidates":[{"content":{"role":"model","parts":[{"text":" """;
        const string suffix = """ "}]},"finishReason":"STOP"}]}""";
        var body = Encoding.UTF8.GetBytes(prefix + new string('x', BodyBytes - prefix.Length - suffix.Length) + suffix);

        var options = new GeminiClientOptions { ApiKey = "test-api-key", Model = "gemini-2.5-flash" };
        _knownLengthClient = new GeminiClient(options, new HttpClient(new CannedResponseHandler(() => new ByteArrayContent(body))));
        _unknownLengthClient = new GeminiClient(
            options,
            new HttpClient(new CannedResponseHandler(() => new StreamContent(new ChunkedMemoryStream(body, 16 * 1024)))));
        _request = GenerateContentRequest.FromPrompt("hello");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _knownLengthClient.Dispose();
        _unknownLengthClient.Dispose();
    }

    [Benchmark]
    public Task<GenerateContentResponse> KnownContentLength() => _knownLengthClient.GenerateContentAsync(_request);

    [Benchmark]
    public Task<GenerateContentResponse> UnknownContentLength() => _unknownLengthClient.GenerateContentAsync(_request);
}

/// <summary>
/// End-to-end streaming through <see cref="IChatClient"/> with production defaults: the idle watchdog armed and the body
/// delivered in 512-byte fragments, so events straddle reads.
/// </summary>
[MemoryDiagnoser]
public class StreamingPipelineBenchmarks
{
    [Params(1, 16, 64)]
    public int ChunkCount { get; set; }

    private GeminiClient _client = null!;
    private IChatClient _chatClient = null!;
    private List<ChatMessage> _messages = null!;

    [GlobalSetup]
    public void Setup()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < ChunkCount; i++)
        {
            var finishReason = i == ChunkCount - 1 ? ""","finishReason":"STOP" """ : string.Empty;
            builder.Append("data: ")
                .Append($$"""{"responseId":"r","modelVersion":"gemini-2.5-flash","candidates":[{"content":{"role":"model","parts":[{"text":"チャンク {{i}} の本文 streamed token text."}]}{{finishReason}}}]}""")
                .Append("\r\n\r\n");
        }

        var body = Encoding.UTF8.GetBytes(builder.ToString());
        _client = new GeminiClient(
            new GeminiClientOptions { ApiKey = "test-api-key", Model = "gemini-2.5-flash" },
            new HttpClient(new CannedResponseHandler(() => new StreamContent(new ChunkedMemoryStream(body, 512)))));
        _chatClient = _client.AsIChatClient();
        _messages = [new ChatMessage(ChatRole.User, "Stream a long answer.")];
    }

    [GlobalCleanup]
    public void Cleanup() => _client.Dispose();

    [Benchmark]
    public async Task<int> EndToEnd_StreamingText()
    {
        var count = 0;
        await foreach (var _ in _chatClient.GetStreamingResponseAsync(_messages))
        {
            count++;
        }

        return count;
    }
}

/// <summary>
/// Tool-value conversion for each result shape, so an improvement to the common shapes cannot hide a regression in the others.
/// </summary>
[MemoryDiagnoser]
public class ToolJsonConverterBenchmarks
{
    public sealed record WeatherReport(string City, int TemperatureCelsius, bool Sunny);

    private readonly JsonSerializerOptions _options = AIJsonUtilities.DefaultOptions;
    private readonly Dictionary<string, object?> _dictionaryResult = new() { ["city"] = "Tokyo", ["temperature"] = 18, ["sunny"] = true };
    private readonly WeatherReport _pocoResult = new("Tokyo", 18, true);
    private readonly object _stringElementResult = JsonElement.Parse("\"18C and sunny\"");
    private readonly object _objectElementResult = JsonElement.Parse("""{"city":"Tokyo","temperature":18}""");
    private readonly JsonElement _fourArguments = JsonElement.Parse("""{"city":"Tokyo","metric":"temperature","units":"celsius","days":5}""");
    private readonly JsonElement _twelveArguments = JsonElement.Parse(
        """{"a":1,"b":"two","c":3.5,"d":true,"e":null,"f":[1,2],"g":{"h":1},"i":"nine","j":10,"k":"eleven","l":12,"m":"thirteen"}""");

    [Benchmark]
    public JsonElement ToResponseObject_String() => ToolJsonConverter.ToResponseObject("18C and sunny", _options);

    [Benchmark]
    public JsonElement ToResponseObject_JsonElementString() => ToolJsonConverter.ToResponseObject(_stringElementResult, _options);

    [Benchmark]
    public JsonElement ToResponseObject_JsonElementObject() => ToolJsonConverter.ToResponseObject(_objectElementResult, _options);

    [Benchmark]
    public JsonElement ToResponseObject_Dictionary() => ToolJsonConverter.ToResponseObject(_dictionaryResult, _options);

    [Benchmark]
    public JsonElement ToResponseObject_Poco() => ToolJsonConverter.ToResponseObject(_pocoResult, _options);

    [Benchmark]
    public JsonElement ToResponseObject_Null() => ToolJsonConverter.ToResponseObject(null, _options);

    [Benchmark]
    public IDictionary<string, object?>? ToDictionary_FourArguments() => ToolJsonConverter.ToDictionary(_fourArguments);

    [Benchmark]
    public IDictionary<string, object?>? ToDictionary_TwelveArguments() => ToolJsonConverter.ToDictionary(_twelveArguments);
}

/// <summary>
/// Returns a fresh canned body after draining the request content, so request serialization is part of every measured call.
/// </summary>
internal sealed class CannedResponseHandler(Func<HttpContent> contentFactory) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await request.Content!.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = contentFactory() };
    }
}
