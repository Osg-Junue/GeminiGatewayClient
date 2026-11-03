using System.Text.Json;
using BenchmarkDotNet.Attributes;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Http;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Benchmarks;

[MemoryDiagnoser]
public class ChatMapperBenchmarks
{
    private const string ModelId = "gemini-2.5-flash";

    private GeminiClient _geminiClient = null!;
    private IChatClient _chatClient = null!;
    private JsonSerializerOptions _toolSerializerOptions = null!;
    private GenerateContentResponse _singlePartResponse = null!;
    private GenerateContentResponse _multiPartResponse = null!;
    private List<ChatMessage> _multiTurnMessages = null!;
    private List<ChatMessage> _systemAndToolMessages = null!;
    private ChatOptions _systemOptions = null!;
    private ChatOptions _toolOptions = null!;
    private GeminiClientOptions _clientOptionsWithHeaders = null!;

    [GlobalSetup]
    public void Setup()
    {
        _geminiClient = new GeminiClient("test-api-key", ModelId);
        _chatClient = _geminiClient.AsIChatClient();
        _toolSerializerOptions = AIJsonUtilities.DefaultOptions;

        _singlePartResponse = new GenerateContentResponse
        {
            ResponseId = "resp-1",
            ModelVersion = ModelId,
            Candidates =
            [
                new Candidate
                {
                    FinishReason = FinishReason.Stop,
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [Part.FromText("The quick brown fox jumps over the lazy dog.")]
                    }
                }
            ],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 12,
                CandidatesTokenCount = 10,
                TotalTokenCount = 22
            }
        };

        var callArgs = JsonDocument.Parse("""{"city":"Tokyo","unit":"celsius"}""").RootElement.Clone();
        _multiPartResponse = new GenerateContentResponse
        {
            ResponseId = "resp-multi",
            ModelVersion = ModelId,
            Candidates =
            [
                new Candidate
                {
                    FinishReason = FinishReason.Stop,
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts =
                        [
                            new Part { Text = "Checking weather across 4 cities...", Thought = true, ThoughtSignature = "sig-thought-1" },
                            new Part { FunctionCall = new FunctionCall { Id = "call-1", Name = "get_weather", Args = callArgs }, ThoughtSignature = "sig-call-1" },
                            new Part { FunctionCall = new FunctionCall { Id = "call-2", Name = "get_weather", Args = callArgs } },
                            new Part { FunctionCall = new FunctionCall { Id = "call-3", Name = "get_weather", Args = callArgs } },
                            new Part { FunctionCall = new FunctionCall { Id = "call-4", Name = "get_weather", Args = callArgs } },
                            Part.FromText("Fetching results now.")
                        ]
                    }
                }
            ]
        };

        _multiTurnMessages =
        [
            new ChatMessage(ChatRole.User, "Hello, can you help me plan a trip to Japan?"),
            new ChatMessage(ChatRole.Assistant, "Of course! How many days are you planning to visit?"),
            new ChatMessage(ChatRole.User, "About 7 days in November, visiting Tokyo and Kyoto."),
            new ChatMessage(ChatRole.Assistant, "November is a great time for autumn foliage. Do you prefer bullet trains?"),
            new ChatMessage(ChatRole.User, "Yes, Shinkansen between Tokyo and Kyoto."),
            new ChatMessage(ChatRole.Assistant, "Great, let's break down the 7-day itinerary.")
        ];

        _systemOptions = new ChatOptions
        {
            Instructions = "You are a concise travel assistant.",
            Temperature = 0.3f,
            MaxOutputTokens = 512
        };

        _systemAndToolMessages =
        [
            new ChatMessage(ChatRole.System, "Always use metric units."),
            new ChatMessage(ChatRole.User, "Compare Tokyo and Kyoto weather."),
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?> { ["city"] = "Tokyo" }),
                new FunctionCallContent("call-2", "get_weather", new Dictionary<string, object?> { ["city"] = "Kyoto" })
            ]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "18C")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-2", "16C")])
        ];

        _toolOptions = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create((string city) => city, "tool_1", "Tool 1"),
                AIFunctionFactory.Create((string city) => city, "tool_2", "Tool 2"),
                AIFunctionFactory.Create((string city) => city, "tool_3", "Tool 3"),
                AIFunctionFactory.Create((string city) => city, "tool_4", "Tool 4"),
                AIFunctionFactory.Create((string city) => city, "tool_5", "Tool 5"),
                AIFunctionFactory.Create((string city) => city, "tool_6", "Tool 6"),
                AIFunctionFactory.Create(() => "pong", "ping", "Ping tool"),
                new HostedWebSearchTool()
            ]
        };

        _clientOptionsWithHeaders = new GeminiClientOptions
        {
            ApiKey = "test-api-key",
            Model = ModelId,
            AdditionalHeaders =
            {
                ["X-Gateway-Tenant"] = "tenant-a",
                ["X-Gateway-Region"] = "asia-northeast1",
                ["X-Correlation-Id"] = "corr-12345"
            }
        };
    }

    [GlobalCleanup]
    public void Cleanup() => _geminiClient.Dispose();

    [Benchmark]
    public ChatResponse MapResponse_SinglePart() =>
        ChatResponseMapper.ToChatResponse(_singlePartResponse, ModelId);

    [Benchmark]
    public ChatResponseUpdate MapStreamChunk_SinglePart() =>
        ChatResponseMapper.ToChatResponseUpdate(_singlePartResponse, ModelId);

    [Benchmark]
    public ChatResponse MapResponse_MultiPartWithToolCalls() =>
        ChatResponseMapper.ToChatResponse(_multiPartResponse, ModelId);

    [Benchmark]
    public GenerateContentRequest ToRequest_MultiTurnNoSystem() =>
        ChatRequestMapper.ToRequest(_multiTurnMessages, options: null, _chatClient, ModelId, _toolSerializerOptions);

    [Benchmark]
    public GenerateContentRequest ToRequest_SystemAndMergedToolTurns() =>
        ChatRequestMapper.ToRequest(_systemAndToolMessages, _systemOptions, _chatClient, ModelId, _toolSerializerOptions);

    [Benchmark]
    public GenerateContentRequest ApplyTools_MultipleFunctionsAndWebSearch()
    {
        var request = new GenerateContentRequest();
        ChatOptionsMapper.Apply(request, _toolOptions, ModelId);
        return request;
    }

    [Benchmark]
    public object CreateRequestHeaders() =>
        GeminiRequestHeaders.Create(_clientOptionsWithHeaders);
}
