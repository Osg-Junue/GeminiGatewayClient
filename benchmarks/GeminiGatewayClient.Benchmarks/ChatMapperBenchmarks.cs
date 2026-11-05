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
    private GenerateContentResponse _groundedResponse = null!;
    private GenerateContentResponse _hybridResponseForNormalization = null!;
    private GenerateContentResponse _groundedJapaneseResponse = null!;

    private List<ChatMessage> _multiTurnMessages = null!;
    private List<ChatMessage> _systemAndToolMessages = null!;
    private List<ChatMessage> _replayedToolHistoryMessages = null!;
    private List<ChatMessage> _toolArgumentsSerializedMessages = null!;
    private List<ChatMessage> _toolArgumentsRawReusedMessages = null!;
    private ChatOptions _systemOptions = null!;
    private ChatOptions _toolOptions = null!;
    private GeminiClientOptions _clientOptionsWithHeaders = null!;
    private GeminiRequestHeaders _requestHeaders = null!;

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

        const string candidateText = "Tokyo and Kyoto weather forecast for the week ahead with detailed temperature and precipitation data.";

        var groundingChunks = new List<GroundingChunk>(8);
        for (var i = 0; i < 8; i++)
        {
            groundingChunks.Add(new GroundingChunk
            {
                Web = new GroundingChunkWeb
                {
                    Uri = $"https://weather.example.com/source-{i}",
                    Title = $"Weather Source {i}"
                }
            });
        }

        (int start, int length)[] segments =
        [
            (0, 5),   // "Tokyo"
            (10, 5),  // "Kyoto"
            (16, 16), // "weather forecast"
            (37, 14), // "the week ahead"
            (57, 20), // "detailed temperature"
            (82, 18), // "precipitation data"
            (0, 32),  // "Tokyo and Kyoto weather forecast"
            (66, 11)  // "temperature"
        ];

        var groundingSupports = new List<GroundingSupport>(16);
        for (var i = 0; i < 16; i++)
        {
            var (start, len) = segments[i % segments.Length];
            groundingSupports.Add(new GroundingSupport
            {
                Segment = new GroundingSegment
                {
                    PartIndex = 0,
                    StartIndex = start,
                    EndIndex = start + len,
                    Text = candidateText.Substring(start, len)
                },
                GroundingChunkIndices = [i % 8, (i + 1) % 8]
            });
        }

        _groundedResponse = new GenerateContentResponse
        {
            ResponseId = "resp-grounded",
            ModelVersion = ModelId,
            Candidates =
            [
                new Candidate
                {
                    FinishReason = FinishReason.Stop,
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [Part.FromText(candidateText)]
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        WebSearchQueries = ["tokyo weather", "kyoto weather"],
                        GroundingChunks = groundingChunks,
                        GroundingSupports = groundingSupports
                    }
                }
            ]
        };

        _groundedJapaneseResponse = CreateGroundedJapaneseResponse();

        _hybridResponseForNormalization = new GenerateContentResponse
        {
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
                            new Part { Text = "Thinking...", Thought = true, ThoughtSignature = "sig-t" },
                            new Part { Text = string.Empty, FunctionCall = new FunctionCall { Id = "call-1", Name = "get_weather", Args = callArgs }, ThoughtSignature = "sig-fc" },
                            new Part { Text = string.Empty }
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

        var callArgsTurn2 = JsonDocument.Parse("""{"city":"Osaka","unit":"celsius"}""").RootElement.Clone();
        var multiPartResponseTurn2 = new GenerateContentResponse
        {
            ResponseId = "resp-multi-2",
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
                            new Part { Text = "Checking tomorrow's weather across cities...", Thought = true, ThoughtSignature = "sig-thought-2" },
                            new Part { FunctionCall = new FunctionCall { Id = "call-5", Name = "get_weather", Args = callArgsTurn2 }, ThoughtSignature = "sig-call-5" },
                            new Part { FunctionCall = new FunctionCall { Id = "call-6", Name = "get_weather", Args = callArgsTurn2 } },
                            new Part { FunctionCall = new FunctionCall { Id = "call-7", Name = "get_weather", Args = callArgsTurn2 } },
                            new Part { FunctionCall = new FunctionCall { Id = "call-8", Name = "get_weather", Args = callArgsTurn2 } },
                            Part.FromText("Fetching tomorrow's results now.")
                        ]
                    }
                }
            ]
        };

        var mappedAssistantToolTurn1 = ChatResponseMapper.ToChatResponse(_multiPartResponse, ModelId).Messages[0];
        var mappedAssistantToolTurn2 = ChatResponseMapper.ToChatResponse(multiPartResponseTurn2, ModelId).Messages[0];

        _replayedToolHistoryMessages =
        [
            new ChatMessage(ChatRole.System, "Always use metric units."),
            new ChatMessage(ChatRole.User, "Check weather across cities."),
            mappedAssistantToolTurn1,
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "18C")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-2", "16C")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-3", "20C")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-4", "14C")]),
            new ChatMessage(ChatRole.User, "Check again for tomorrow."),
            mappedAssistantToolTurn2,
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-5", "19C")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-6", "17C")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-7", "21C")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-8", "15C")]),
            new ChatMessage(ChatRole.User, "Summarize the 2-day trend.")
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

        var rawToolArgs = JsonDocument.Parse("""{"city":"Tokyo","metric":"temperature","units":"celsius","forecastDays":5}""").RootElement.Clone();
        var rawPartWithArgs = new Part
        {
            FunctionCall = new FunctionCall
            {
                Id = "call-fair-1",
                Name = "get_weather_forecast",
                Args = rawToolArgs
            }
        };

        // Raw args reuse path: replayed FunctionCallContent whose arguments are the JsonElement values produced by
        // ChatResponseMapper. Plain CLR values would fail AreUnmodifiedArguments and silently measure re-serialization instead.
        _toolArgumentsRawReusedMessages =
        [
            new ChatMessage(ChatRole.User, "Check Tokyo weather forecast for 5 days."),
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-fair-1", "get_weather_forecast", ToolJsonConverter.ToDictionary(rawToolArgs))
                {
                    RawRepresentation = rawPartWithArgs
                }
            ]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-fair-1", "20C sunny")])
        ];

        // Normal serialization path (Baseline): FunctionCallContent with dictionary args, no RawRepresentation
        _toolArgumentsSerializedMessages =
        [
            new ChatMessage(ChatRole.User, "Check Tokyo weather forecast for 5 days."),
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-fair-1", "get_weather_forecast", new Dictionary<string, object?>
                {
                    ["city"] = "Tokyo",
                    ["metric"] = "temperature",
                    ["units"] = "celsius",
                    ["forecastDays"] = 5
                })
            ]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-fair-1", "20C sunny")])
        ];

        _requestHeaders = GeminiRequestHeaders.Create(_clientOptionsWithHeaders);
    }

    [GlobalCleanup]
    public void Cleanup() => _geminiClient.Dispose();

    /// <summary>
    /// A ~1,000-character multi-byte answer with evenly spread supports, exercising the non-ASCII UTF-8 to UTF-16 offset conversion.
    /// </summary>
    private static GenerateContentResponse CreateGroundedJapaneseResponse()
    {
        const int sentenceCount = 32;
        const string sentence = "東京と京都の今週の天気予報について詳しく説明します🌸。";
        var sentenceBytes = System.Text.Encoding.UTF8.GetByteCount(sentence);

        var supports = new List<GroundingSupport>(sentenceCount);
        for (var i = 0; i < sentenceCount; i++)
        {
            supports.Add(new GroundingSupport
            {
                Segment = new GroundingSegment { PartIndex = 0, StartIndex = i * sentenceBytes, EndIndex = (i + 1) * sentenceBytes, Text = sentence },
                GroundingChunkIndices = [i % 8]
            });
        }

        var chunks = new List<GroundingChunk>(8);
        for (var i = 0; i < 8; i++)
        {
            chunks.Add(new GroundingChunk { Web = new GroundingChunkWeb { Uri = $"https://weather.example.jp/{i}", Title = $"天気 {i}" } });
        }

        return new GenerateContentResponse
        {
            ResponseId = "resp-grounded-ja",
            ModelVersion = ModelId,
            Candidates =
            [
                new Candidate
                {
                    FinishReason = FinishReason.Stop,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText(string.Concat(Enumerable.Repeat(sentence, sentenceCount)))] },
                    GroundingMetadata = new GroundingMetadata { GroundingChunks = chunks, GroundingSupports = supports }
                }
            ]
        };
    }

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
    public ChatResponse MapResponse_GroundingWithCitations() =>
        ChatResponseMapper.ToChatResponse(_groundedResponse, ModelId);

    [Benchmark]
    public ChatResponse MapResponse_GroundingJapanese() =>
        ChatResponseMapper.ToChatResponse(_groundedJapaneseResponse, ModelId);

    [Benchmark]
    public GenerateContentRequest ToRequest_MultiTurnNoSystem() =>
        ChatRequestMapper.ToRequest(_multiTurnMessages, options: null, _chatClient, ModelId, _toolSerializerOptions);

    [Benchmark]
    public GenerateContentRequest ToRequest_SystemAndMergedToolTurns() =>
        ChatRequestMapper.ToRequest(_systemAndToolMessages, _systemOptions, _chatClient, ModelId, _toolSerializerOptions);

    [Benchmark]
    public GenerateContentRequest ToRequest_MultiTurnToolHistoryReplay() =>
        ChatRequestMapper.ToRequest(_replayedToolHistoryMessages, _systemOptions, _chatClient, ModelId, _toolSerializerOptions);

    [Benchmark(Baseline = true)]
    public GenerateContentRequest ToRequest_ToolArguments_DictionarySerialization() =>
        ChatRequestMapper.ToRequest(_toolArgumentsSerializedMessages, options: null, _chatClient, ModelId, _toolSerializerOptions);

    [Benchmark]
    public GenerateContentRequest ToRequest_ToolArguments_RawArgsReuse() =>
        ChatRequestMapper.ToRequest(_toolArgumentsRawReusedMessages, options: null, _chatClient, ModelId, _toolSerializerOptions);

    [Benchmark]
    public Content? TryGetModelContent_Clean() =>
        _singlePartResponse.TryGetModelContent(out var content) ? content : null;

    [Benchmark]
    public Content? TryGetModelContent_Normalized() =>
        _hybridResponseForNormalization.TryGetModelContent(out var content) ? content : null;

    [Benchmark]
    public GenerateContentRequest ApplyTools_MultipleFunctionsAndWebSearch()
    {
        var request = new GenerateContentRequest();
        ChatOptionsMapper.Apply(request, _toolOptions, ModelId);
        return request;
    }

    [Benchmark]
    public async ValueTask ApplyHeaders_PerRequest()
    {
        using var requestMessage = new HttpRequestMessage();
        await _requestHeaders.ApplyAsync(requestMessage, CancellationToken.None);
    }
}
