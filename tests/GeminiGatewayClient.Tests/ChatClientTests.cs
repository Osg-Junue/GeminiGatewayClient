using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Tests;

public partial class ChatClientTests
{
    private const string DefaultModel = "gemini-2.5-flash";

    private const string OkResponseJson = """
        {
          "candidates": [
            {
              "index": 0,
              "content": {
                "role": "model",
                "parts": [
                  { "text": "Hello from Gemini!" }
                ]
              },
              "finishReason": "STOP"
            }
          ],
          "usageMetadata": {
            "promptTokenCount": 5,
            "candidatesTokenCount": 4,
            "totalTokenCount": 14,
            "thoughtsTokenCount": 3,
            "cachedContentTokenCount": 2,
            "toolUsePromptTokenCount": 2
          },
          "modelVersion": "gemini-2.5-flash",
          "responseId": "resp-abc"
        }
        """;

    [Fact]
    public async Task IChatClient_GetResponseAsync_MapsMessagesOptionsUsageAndMetadata()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var metadata = chatClient.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("gemini-2.5-flash", metadata.DefaultModelId);
        Assert.Same(geminiClient, chatClient.GetService<IGeminiClient>());
        Assert.Same(geminiClient, chatClient.GetService<GeminiClient>());

        var chatResponse = await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "Be concise."),
            new ChatMessage(ChatRole.User, "Hello!")
        ], new ChatOptions
        {
            Temperature = 0.5f,
            MaxOutputTokens = 128
        });

        Assert.Equal("Hello from Gemini!", chatResponse.Text);
        Assert.Equal("resp-abc", chatResponse.ResponseId);
        Assert.Equal(ChatFinishReason.Stop, chatResponse.FinishReason);
        Assert.Equal(7, chatResponse.Usage?.InputTokenCount);        // 5 prompt + 2 toolUsePrompt
        Assert.Equal(7, chatResponse.Usage?.OutputTokenCount);       // 4 candidates + 3 thoughts
        Assert.Equal(14, chatResponse.Usage?.TotalTokenCount);
        Assert.Equal(3, chatResponse.Usage?.ReasoningTokenCount);
        Assert.Equal(2, chatResponse.Usage?.CachedInputTokenCount);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal(
            "Be concise.",
            sentDoc.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(
            "Hello!",
            sentDoc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(
            128,
            sentDoc.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
    }

    [Fact]
    public async Task IChatClient_GetStreamingResponseAsync_YieldsUpdatesOverSse()
    {
        const string sseJson = """
            data: {"responseId":"stream-1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Stream "}]}}]}

            data: {"responseId":"stream-1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"complete."}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5}}

            """;

        using var handler = new RecordingHandler(sseJson, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var sb = new StringBuilder();
        ChatFinishReason? finishReason = null;
        string? responseId = null;
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Test")]))
        {
            sb.Append(update.Text);
            finishReason ??= update.FinishReason;
            responseId ??= update.ResponseId;
        }

        Assert.Equal("Stream complete.", sb.ToString());
        Assert.Equal(ChatFinishReason.Stop, finishReason);
        Assert.Equal("stream-1", responseId);
    }

    [Fact]
    public async Task IChatClient_ParallelFunctionCalls_PreserveDistinctIdsAndRoundTrip()
    {
        const string parallelFunctionCallResponseJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "functionCall": {
                          "id": "call_tokyo",
                          "name": "get_weather",
                          "args": { "city": "Tokyo" }
                        },
                        "thoughtSignature": "sig-1"
                      },
                      {
                        "functionCall": {
                          "id": "call_osaka",
                          "name": "get_weather",
                          "args": { "city": "Osaka" }
                        }
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(parallelFunctionCallResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var firstTurn = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Weather in Tokyo and Osaka?")]);
        Assert.Equal(ChatFinishReason.ToolCalls, firstTurn.FinishReason);

        var calls = firstTurn.Messages[0].Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("call_tokyo", calls[0].CallId);
        Assert.Equal("call_osaka", calls[1].CallId);
        Assert.Equal("Tokyo", Assert.IsType<JsonElement>(calls[0].Arguments!["city"]).GetString());
        Assert.Equal("Osaka", Assert.IsType<JsonElement>(calls[1].Arguments!["city"]).GetString());

        // Round-trip both assistant calls and tool results
        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Weather in Tokyo and Osaka?"),
            firstTurn.Messages[0],
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call_tokyo", new Dictionary<string, object?> { ["tempC"] = 22 }),
                new FunctionResultContent("call_osaka", new Dictionary<string, object?> { ["tempC"] = 25 })
            ])
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var contents = sentDoc.RootElement.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());

        // Assistant turn preserves thoughtSignature on first part
        var modelParts = contents[1].GetProperty("parts");
        Assert.Equal("sig-1", modelParts[0].GetProperty("thoughtSignature").GetString());

        // Tool response turn correlates both call IDs back to get_weather
        var toolParts = contents[2].GetProperty("parts");
        Assert.Equal("call_tokyo", toolParts[0].GetProperty("functionResponse").GetProperty("id").GetString());
        Assert.Equal("get_weather", toolParts[0].GetProperty("functionResponse").GetProperty("name").GetString());
        Assert.Equal(22, toolParts[0].GetProperty("functionResponse").GetProperty("response").GetProperty("tempC").GetInt32());

        Assert.Equal("call_osaka", toolParts[1].GetProperty("functionResponse").GetProperty("id").GetString());
        Assert.Equal("get_weather", toolParts[1].GetProperty("functionResponse").GetProperty("name").GetString());
        Assert.Equal(25, toolParts[1].GetProperty("functionResponse").GetProperty("response").GetProperty("tempC").GetInt32());
    }

    [Fact]
    public async Task IChatClient_ParallelFunctionCallsWithoutServerId_GenerateDistinctCallIds()
    {
        const string noIdCallsJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      { "functionCall": { "name": "get_weather", "args": { "city": "Tokyo" } } },
                      { "functionCall": { "name": "get_weather", "args": { "city": "Osaka" } } }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(noIdCallsJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Compare")]);
        var calls = response.Messages[0].Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.NotEqual(calls[0].CallId, calls[1].CallId);
    }

    [Fact]
    public async Task IChatClient_ThoughtAndSignatureOnlyParts_RoundTripViaProtectedData()
    {
        const string thinkingResponseJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      { "text": "Step 1...", "thought": true, "thoughtSignature": "sig-thought" },
                      { "text": "Final answer." },
                      { "thoughtSignature": "sig-trailing" }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(thinkingResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Think")]);
        var reasoningItems = response.Messages[0].Contents.OfType<TextReasoningContent>().ToList();
        Assert.Equal(2, reasoningItems.Count);
        Assert.Equal("Step 1...", reasoningItems[0].Text);
        Assert.Equal("sig-thought", reasoningItems[0].ProtectedData);
        Assert.Equal(string.Empty, reasoningItems[1].Text);
        Assert.Equal("sig-trailing", reasoningItems[1].ProtectedData);

        // Recreate without RawRepresentation (simulating serialization/deserialization of ChatMessage history)
        var reconstructedAssistant = new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent("Step 1...") { ProtectedData = "sig-thought" },
            new TextContent("Final answer."),
            new TextReasoningContent(string.Empty) { ProtectedData = "sig-trailing" }
        ]);

        await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Think"), reconstructedAssistant, new ChatMessage(ChatRole.User, "Next")]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var assistantParts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(3, assistantParts.GetArrayLength());
        Assert.True(assistantParts[0].GetProperty("thought").GetBoolean());
        Assert.Equal("sig-thought", assistantParts[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("sig-trailing", assistantParts[2].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task IChatClient_StructuredOutputAndTools_UseJsonSchemaDirectly_AndOmitEmptyToolParameters()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        using var responseSchemaDoc = JsonDocument.Parse("""
            {
              "type": "object",
              "$defs": { "Item": { "type": "string" } },
              "properties": {
                "value": { "anyOf": [ { "$ref": "#/$defs/Item" }, { "type": "null" } ] }
              },
              "required": ["value"]
            }
            """);

        var paramlessTool = AIFunctionFactory.Create(() => "pong", "ping", "Pings the server");
        var paramTool = AIFunctionFactory.Create((string city) => city, "get_weather", "Gets weather");

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Run")],
            new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema(responseSchemaDoc.RootElement, "MySchema"),
                Tools = [paramlessTool, paramTool]
            });

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var genConfig = sentDoc.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", genConfig.GetProperty("responseMimeType").GetString());
        Assert.True(genConfig.TryGetProperty("responseJsonSchema", out var rjs));
        Assert.True(rjs.TryGetProperty("$defs", out _));

        var declarations = sentDoc.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations");
        Assert.Equal(2, declarations.GetArrayLength());

        // Parameterless function must omit parametersJsonSchema
        Assert.Equal("ping", declarations[0].GetProperty("name").GetString());
        Assert.False(declarations[0].TryGetProperty("parametersJsonSchema", out _));
        Assert.False(declarations[0].TryGetProperty("parameters", out _));

        // Parameterized function preserves parametersJsonSchema
        Assert.Equal("get_weather", declarations[1].GetProperty("name").GetString());
        Assert.True(declarations[1].TryGetProperty("parametersJsonSchema", out var pjs));
        Assert.True(pjs.GetProperty("properties").TryGetProperty("city", out _));
    }

    [Fact]
    public async Task IChatClient_PocoToolResultAndDateTime_SerializeCleanly()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        var serializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = WeatherReportJsonContext.Default
        };

        using var geminiClient = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient(toolSerializerOptions: serializerOptions);

        var timestamp = new DateTime(2026, 11, 2, 12, 30, 0, DateTimeKind.Utc);
        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Weather?"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "get_weather")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new WeatherReport("Tokyo", 21.5, timestamp))])
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var responseObj = sentDoc.RootElement
            .GetProperty("contents")[2]
            .GetProperty("parts")[0]
            .GetProperty("functionResponse")
            .GetProperty("response");

        Assert.Equal("Tokyo", responseObj.GetProperty("city").GetString());
        Assert.Equal(21.5, responseObj.GetProperty("tempC").GetDouble());
        Assert.Equal("2026-11-02T12:30:00Z", responseObj.GetProperty("observedAtUtc").GetString());
    }

    [Fact]
    public async Task IChatClient_DataContentWithArbitraryMimeType_IsForwardedNotDropped()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent("Describe this HEIC photo"),
            new DataContent(new byte[] { 1, 2, 3, 4 }, "image/heic")
        ]);

        await chatClient.GetResponseAsync([message]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var parts = sentDoc.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("image/heic", parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), parts[1].GetProperty("inlineData").GetProperty("data").GetString());
    }

    [Fact]
    public async Task IChatClient_ModelIdAndReasoningOptions_OverrideUrlAndMapThinkingConfig()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Solve")],
            new ChatOptions
            {
                ModelId = "gemini-3-flash-preview",
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.Low,
                    Output = ReasoningOutput.Summary
                }
            });

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent",
            handler.LastRequest?.RequestUri?.ToString());

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var thinkingConfig = sentDoc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.Equal("LOW", thinkingConfig.GetProperty("thinkingLevel").GetString());
        Assert.True(thinkingConfig.GetProperty("includeThoughts").GetBoolean());
        Assert.False(thinkingConfig.TryGetProperty("thinkingBudget", out _));
    }

    [Fact]
    public async Task IChatClient_OutOfRangeSeed_ThrowsArgumentOutOfRangeException()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Hi")],
                new ChatOptions { Seed = (long)int.MaxValue + 10 }));
    }

    [Fact]
    public async Task IChatClient_PromptBlockedBySafety_MapsToContentFilterFinishReason()
    {
        const string blockedJson = """
            {
              "promptFeedback": {
                "blockReason": "SAFETY",
                "safetyRatings": [
                  { "category": "HARM_CATEGORY_HATE_SPEECH", "probability": "HIGH" }
                ]
              }
            }
            """;

        using var handler = new RecordingHandler(blockedJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Blocked prompt")]);
        Assert.Equal(ChatFinishReason.ContentFilter, response.FinishReason);
        Assert.Equal("Safety", response.AdditionalProperties?[GeminiChatPropertyNames.BlockReason]);
    }

    [Fact]
    public async Task IChatClient_StreamingUpdatesAggregatedViaToChatResponse_PreserveFunctionCallThoughtSignature()
    {
        const string sseWithToolCallAndSignature = """
            data: {"responseId":"r-stream","candidates":[{"index":0,"content":{"role":"model","parts":[{"functionCall":{"id":"call-99","name":"get_weather","args":{"city":"Kyoto"}},"thoughtSignature":"sig-stream-99"}]},"finishReason":"STOP"}]}

            """;

        using var handler = new RecordingHandler(sseWithToolCallAndSignature, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Kyoto weather?")]))
        {
            updates.Add(update);
        }

        var aggregated = updates.ToChatResponse();
        await foreach (var _ in chatClient.GetStreamingResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Kyoto weather?"),
            .. aggregated.Messages,
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-99", new Dictionary<string, object?> { ["tempC"] = 19 })])
        ]))
        {
        }

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var modelPart = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts")[0];
        Assert.Equal("sig-stream-99", modelPart.GetProperty("thoughtSignature").GetString());
        Assert.Equal("call-99", modelPart.GetProperty("functionCall").GetProperty("id").GetString());
    }

    [Fact]
    public async Task IChatClient_CustomGatewayModelId_MapsReasoningEffortToThinkingLevel()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Reason")],
            new ChatOptions
            {
                ModelId = "tunedModels/my-enterprise-model",
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium }
            });

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var thinkingConfig = sentDoc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.Equal("MEDIUM", thinkingConfig.GetProperty("thinkingLevel").GetString());
    }

    [Fact]
    public async Task IChatClient_MutatedTextContentWithRawRepresentation_UsesUpdatedTextAndPreservesVideoMetadata()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var rawPart = new GeminiGatewayClient.Models.Part
        {
            Text = "original text",
            ThoughtSignature = "sig-original",
            VideoMetadata = new GeminiGatewayClient.Models.VideoMetadata
            {
                StartOffset = new GeminiGatewayClient.Models.VideoOffset { Seconds = 5 },
                Fps = 2.0
            }
        };

        var textContent = new TextContent("updated text") { RawRepresentation = rawPart };
        await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, [textContent])]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var sentPart = sentDoc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0];
        Assert.Equal("updated text", sentPart.GetProperty("text").GetString());
        Assert.Equal("sig-original", sentPart.GetProperty("thoughtSignature").GetString());
        Assert.Equal("5s", sentPart.GetProperty("videoMetadata").GetProperty("startOffset").GetString());
        Assert.Equal(2.0, sentPart.GetProperty("videoMetadata").GetProperty("fps").GetDouble());
    }

    [Fact]
    public async Task IChatClient_MapsGroundingMetadataAndCitationMetadata_WithMultibyteUtf8OffsetsToCharSpans()
    {
        // "東京の天気は晴れです" -> 10 Japanese chars, 3 UTF-8 bytes each = 30 UTF-8 bytes.
        // "天気" is char indices [3..5], which is UTF-8 byte offsets [9..15].
        // "晴れ" is char indices [6..8], which is UTF-8 byte offsets [18..24].
        const string groundedJson = """
            {
              "responseId": "resp-grounding-1",
              "modelVersion": "gemini-2.5-flash",
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      { "text": "東京の天気は晴れです" }
                    ]
                  },
                  "finishReason": "STOP",
                  "citationMetadata": {
                    "citations": [
                      {
                        "startIndex": 18,
                        "endIndex": 24,
                        "uri": "https://jma.go.jp/forecast",
                        "title": "Japan Meteorological Agency"
                      }
                    ]
                  },
                  "groundingMetadata": {
                    "webSearchQueries": ["東京 天気 今日"],
                    "groundingChunks": [
                      {
                        "web": {
                          "uri": "https://weather.example.jp/tokyo",
                          "title": "東京の天気予報"
                        }
                      }
                    ],
                    "groundingSupports": [
                      {
                        "segment": {
                          "partIndex": 0,
                          "startIndex": 9,
                          "endIndex": 15,
                          "text": "天気"
                        },
                        "groundingChunkIndices": [0],
                        "confidenceScores": [0.99]
                      }
                    ]
                  }
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(groundedJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "東京の天気は？")]);

        Assert.NotNull(response.AdditionalProperties);
        var queries = Assert.IsAssignableFrom<IList<string>>(response.AdditionalProperties[GeminiChatPropertyNames.WebSearchQueries]);
        Assert.Equal(["東京 天気 今日"], queries);

        var textContent = Assert.Single(response.Messages[0].Contents.OfType<TextContent>());
        Assert.Equal("東京の天気は晴れです", textContent.Text);
        Assert.NotNull(textContent.Annotations);
        Assert.Equal(2, textContent.Annotations.Count);

        var directCitation = Assert.IsType<CitationAnnotation>(textContent.Annotations[0]);
        Assert.Equal("Japan Meteorological Agency", directCitation.Title);
        Assert.Equal(new Uri("https://jma.go.jp/forecast"), directCitation.Url);
        var directRegion = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(directCitation.AnnotatedRegions!));
        Assert.Equal(6, directRegion.StartIndex);
        Assert.Equal(8, directRegion.EndIndex);
        Assert.Equal("晴れ", textContent.Text[directRegion.StartIndex!.Value..directRegion.EndIndex!.Value]);

        var groundingCitation = Assert.IsType<CitationAnnotation>(textContent.Annotations[1]);
        Assert.Equal("東京の天気予報", groundingCitation.Title);
        Assert.Equal(new Uri("https://weather.example.jp/tokyo"), groundingCitation.Url);
        Assert.Equal("google_search", groundingCitation.ToolName);
        Assert.Equal("天気", groundingCitation.Snippet);
        var groundingRegion = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(groundingCitation.AnnotatedRegions!));
        Assert.Equal(3, groundingRegion.StartIndex);
        Assert.Equal(5, groundingRegion.EndIndex);
        Assert.Equal("天気", textContent.Text[groundingRegion.StartIndex!.Value..groundingRegion.EndIndex!.Value]);
    }

    [Fact]
    public async Task IEmbeddingGenerator_SingleAndBatch_RouteToEmbedContentAndBatchEmbedContents()
    {
        const string singleResponseJson = """
            {
              "embedding": {
                "values": [0.1, 0.2, 0.3]
              }
            }
            """;

        using var singleHandler = new RecordingHandler(singleResponseJson);
        using var singleHttpClient = new HttpClient(singleHandler);
        using var singleGeminiClient = new GeminiClient(new GeminiClientOptions
        {
            Model = "gemini-2.5-flash",
            EmbeddingModel = "gemini-embedding-001",
            ApiKey = "test-key"
        }, singleHttpClient);

        IEmbeddingGenerator<string, Embedding<float>> generator = singleGeminiClient.AsIEmbeddingGenerator(defaultModelDimensions: 3);
        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("gemini-embedding-001", metadata.DefaultModelId);
        Assert.Equal(3, metadata.DefaultModelDimensions);
        Assert.Same(singleGeminiClient, generator.GetService<IGeminiClient>());

        var singleResult = await generator.GenerateAsync(
            ["Hello embedding"],
            new EmbeddingGenerationOptions
            {
                RawRepresentationFactory = _ => new GeminiGatewayClient.Models.EmbedContentRequest
                {
                    TaskType = GeminiGatewayClient.Models.TaskType.RetrievalDocument,
                    Title = "Doc Title"
                }
            });

        var singleEmbedding = Assert.Single(singleResult);
        Assert.Equal([0.1f, 0.2f, 0.3f], singleEmbedding.Vector.ToArray());
        Assert.Equal("gemini-embedding-001", singleEmbedding.ModelId);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent",
            singleHandler.LastRequest?.RequestUri?.ToString());

        Assert.NotNull(singleHandler.LastRequestBody);
        using (var sentDoc = JsonDocument.Parse(singleHandler.LastRequestBody))
        {
            Assert.Equal("RETRIEVAL_DOCUMENT", sentDoc.RootElement.GetProperty("taskType").GetString());
            Assert.Equal("Doc Title", sentDoc.RootElement.GetProperty("title").GetString());
            Assert.Equal(3, sentDoc.RootElement.GetProperty("outputDimensionality").GetInt32());
        }

        const string batchResponseJson = """
            {
              "embeddings": [
                { "values": [1.0, 2.0] },
                { "values": [3.0, 4.0] }
              ]
            }
            """;

        using var batchHandler = new RecordingHandler(batchResponseJson);
        using var batchHttpClient = new HttpClient(batchHandler);
        using var batchGeminiClient = new GeminiClient(new GeminiClientOptions
        {
            Model = "gemini-2.5-flash",
            EmbeddingModel = "gemini-embedding-001",
            ApiKey = "test-key"
        }, batchHttpClient);

        var batchGenerator = batchGeminiClient.AsIEmbeddingGenerator();
        var batchResult = await batchGenerator.GenerateAsync(
            ["First chunk", "Second chunk"],
            new EmbeddingGenerationOptions
            {
                ModelId = "text-embedding-004",
                Dimensions = 2
            });

        Assert.Equal(2, batchResult.Count);
        Assert.Equal([1.0f, 2.0f], batchResult[0].Vector.ToArray());
        Assert.Equal([3.0f, 4.0f], batchResult[1].Vector.ToArray());
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:batchEmbedContents",
            batchHandler.LastRequest?.RequestUri?.ToString());

        Assert.NotNull(batchHandler.LastRequestBody);
        using (var batchSentDoc = JsonDocument.Parse(batchHandler.LastRequestBody))
        {
            var reqs = batchSentDoc.RootElement.GetProperty("requests");
            Assert.Equal(2, reqs.GetArrayLength());
            Assert.Equal("models/text-embedding-004", reqs[0].GetProperty("model").GetString());
            Assert.Equal(2, reqs[0].GetProperty("outputDimensionality").GetInt32());
            Assert.Equal("models/text-embedding-004", reqs[1].GetProperty("model").GetString());
        }
    }

    [Fact]
    public async Task EmbedContent_AndBatchEmbedContents_NormalizeBatchModelsWithoutMutatingCallerItems_AndRegisterViaDI()
    {
        const string batchJson = """
            {
              "embeddings": [
                { "values": [0.25, 0.5] },
                { "values": [0.75, 1.0] }
              ]
            }
            """;

        using var handler = new RecordingHandler(batchJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta"),
            Model = DefaultModel,
            EmbeddingModel = "gemini-embedding-001",
            ApiKey = "apim-key",
            ApiKeyHeader = "Ocp-Apim-Subscription-Key"
        }, httpClient);

        var item0 = GeminiGatewayClient.Models.EmbedContentRequest.FromText(
            "First document",
            GeminiGatewayClient.Models.TaskType.RetrievalDocument,
            title: "Doc 1",
            outputDimensionality: 2);
        var item1 = GeminiGatewayClient.Models.EmbedContentRequest.FromText(
            "Second query",
            GeminiGatewayClient.Models.TaskType.RetrievalQuery,
            outputDimensionality: 2);
        item1.Model = "gemini-embedding-001";

        var batchRequest = new GeminiGatewayClient.Models.BatchEmbedContentsRequest
        {
            Requests = [item0, item1]
        };

        var response = await client.BatchEmbedContentsAsync(batchRequest);

        Assert.Null(item0.Model);
        Assert.Equal("gemini-embedding-001", item1.Model);
        Assert.Equal(2, response.Embeddings.Count);
        Assert.Equal([0.25f, 0.5f], response.Embeddings[0].Values);
        Assert.Equal([0.75f, 1.0f], response.Embeddings[1].Values);

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.GeminiServiceCollectionExtensions.AddGeminiClient(services, options =>
        {
            options.Model = "gemini-2.5-flash";
            options.EmbeddingModel = "gemini-embedding-001";
            options.ApiKey = "di-key";
        });

        await using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var diGenerator = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(sp);
        var diMetadata = diGenerator.GetService<EmbeddingGeneratorMetadata>();
        Assert.NotNull(diMetadata);
        Assert.Equal("gemini-embedding-001", diMetadata.DefaultModelId);
    }

    [Fact]
    public async Task GroundingCitations_SurrogatePairsAndMultiPartSegment_MapUtf16SpansAndTargetCorrectTextContent()
    {
        // Part[0]: "Intro: "
        // Part[1]: "😀晴れ"
        //   "😀" (U+1F600): 4 UTF-8 bytes (0..4), 2 UTF-16 chars (0..2)
        //   "晴れ": 6 UTF-8 bytes (4..10), 2 UTF-16 chars (2..4)
        const string multiPartJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      { "text": "Intro: " },
                      { "text": "😀晴れ" }
                    ]
                  },
                  "finishReason": "STOP",
                  "groundingMetadata": {
                    "groundingChunks": [
                      {
                        "web": {
                          "uri": "https://weather.example.jp/emoji",
                          "title": "Emoji Forecast"
                        }
                      }
                    ],
                    "groundingSupports": [
                      {
                        "segment": {
                          "partIndex": 1,
                          "startIndex": 4,
                          "endIndex": 10,
                          "text": "晴れ"
                        },
                        "groundingChunkIndices": [0]
                      }
                    ]
                  }
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(multiPartJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "天気は？")]);

        var textContents = response.Messages[0].Contents.OfType<TextContent>().ToList();
        Assert.Equal(2, textContents.Count);
        Assert.Null(textContents[0].Annotations);

        Assert.NotNull(textContents[1].Annotations);
        var citation = Assert.IsType<CitationAnnotation>(Assert.Single(textContents[1].Annotations!));
        Assert.Equal("Emoji Forecast", citation.Title);
        var region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(citation.AnnotatedRegions!));
        Assert.Equal(2, region.StartIndex);
        Assert.Equal(4, region.EndIndex);
        Assert.Equal("晴れ", textContents[1].Text[region.StartIndex!.Value..region.EndIndex!.Value]);
    }

    [Fact]
    public async Task GeminiEmbeddingGenerator_Validation_BatchTemplateOverride_AndEmptyResponseErrors()
    {
        using var handler = new RecordingHandler("{}");
        using var httpClient = new HttpClient(handler);
        using var geminiClient = new GeminiClient(new GeminiClientOptions
        {
            EmbeddingModel = "gemini-embedding-001",
            ApiKey = "test-key"
        }, httpClient);

        Assert.Throws<ArgumentOutOfRangeException>(() => geminiClient.AsIEmbeddingGenerator(defaultModelDimensions: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => geminiClient.AsIEmbeddingGenerator(defaultModelDimensions: -5));

        var generator = geminiClient.AsIEmbeddingGenerator();

        // Empty and null element checks across both IReadOnlyList<string> and lazy IEnumerable<string>
        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateAsync(Array.Empty<string>()));
        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateAsync(YieldItems()));
        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateAsync(["valid", null!]));
        await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateAsync(YieldItems("valid", null!)));

        // Empty response object {} on single input must throw GeminiApiException instead of returning a 0-dim vector
        await Assert.ThrowsAsync<GeminiApiException>(() => generator.GenerateAsync(["single input"]));

        // Mismatched count on batch response must throw GeminiApiException
        using var mismatchHandler = new RecordingHandler("""{ "embeddings": [ { "values": [0.1, 0.2] } ] }""");
        using var mismatchHttp = new HttpClient(mismatchHandler);
        using var mismatchClient = new GeminiClient(new GeminiClientOptions
        {
            EmbeddingModel = "gemini-embedding-001",
            ApiKey = "test-key"
        }, mismatchHttp);
        var mismatchGen = mismatchClient.AsIEmbeddingGenerator();
        await Assert.ThrowsAsync<GeminiApiException>(() => mismatchGen.GenerateAsync(["one", "two"]));

        // Single input with a BatchEmbedContentsRequest template from RawRepresentationFactory routes to :batchEmbedContents
        var singleAsBatchResult = await mismatchGen.GenerateAsync(
            ["only-one"],
            new EmbeddingGenerationOptions
            {
                RawRepresentationFactory = _ => new GeminiGatewayClient.Models.BatchEmbedContentsRequest()
            });
        Assert.Single(singleAsBatchResult);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents",
            mismatchHandler.LastRequest?.RequestUri?.ToString());

        static IEnumerable<string> YieldItems(params string[] items)
        {
            foreach (var item in items)
            {
                yield return item;
            }
        }
    }

    [Fact]
    public async Task EmbedContent_NormalizesSingleRequestBareModel_AndCachesDefaultEmbeddingEndpointUris()
    {
        var endpoint = GeminiGatewayClient.Http.GeminiEndpoint.Create(
            GeminiClientOptions.DefaultEndpoint,
            defaultModel: "gemini-2.5-flash",
            allowInsecureHttp: false,
            defaultEmbeddingModel: "gemini-embedding-001");

        var uri1 = endpoint.GetEmbeddingOperationUri(null, "embedContent");
        var uri2 = endpoint.GetEmbeddingOperationUri("gemini-embedding-001", "embedContent");
        Assert.Same(uri1, uri2);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent", uri1.ToString());

        var batchUri1 = endpoint.GetEmbeddingOperationUri(null, "batchEmbedContents");
        var batchUri2 = endpoint.GetEmbeddingOperationUri("gemini-embedding-001", "batchEmbedContents");
        Assert.Same(batchUri1, batchUri2);

        const string embedJson = """{ "embedding": { "values": [0.5, 0.25] } }""";
        using var handler = new RecordingHandler(embedJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            EmbeddingModel = "gemini-embedding-001",
            ApiKey = "test-key"
        }, httpClient);

        var request = GeminiGatewayClient.Models.EmbedContentRequest.FromText("hello");
        request.Model = "gemini-embedding-001";

        var response = await client.EmbedContentAsync(request);
        Assert.Equal("gemini-embedding-001", request.Model);
        Assert.Equal([0.5f, 0.25f], response.Embedding!.Values);

        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("models/gemini-embedding-001", sentDoc.RootElement.GetProperty("model").GetString());

        // Oversized Content-Length (> MaxBufferedResponseBytes) must fail fast with GeminiApiException
        using var oversizedHandler = new OversizedContentLengthHandler((long)GeminiClient.MaxBufferedResponseBytes + 1);
        using var oversizedHttp = new HttpClient(oversizedHandler);
        using var oversizedClient = new GeminiClient(new GeminiClientOptions
        {
            EmbeddingModel = "gemini-embedding-001",
            ApiKey = "test-key"
        }, oversizedHttp);

        await Assert.ThrowsAsync<GeminiApiException>(() => oversizedClient.EmbedContentAsync(request));
    }

    private sealed class OversizedContentLengthHandler(long declaredContentLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent("{}"u8.ToArray());
            content.Headers.ContentLength = declaredContentLength;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    public sealed record WeatherReport(
        [property: JsonPropertyName("city")] string City,
        [property: JsonPropertyName("tempC")] double TempC,
        [property: JsonPropertyName("observedAtUtc")] DateTime ObservedAtUtc);

    [JsonSerializable(typeof(WeatherReport))]
    internal partial class WeatherReportJsonContext : JsonSerializerContext
    {
    }
}
