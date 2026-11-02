using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace GeminiAPIWrapper.Tests;

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

    public sealed record WeatherReport(
        [property: JsonPropertyName("city")] string City,
        [property: JsonPropertyName("tempC")] double TempC,
        [property: JsonPropertyName("observedAtUtc")] DateTime ObservedAtUtc);

    [JsonSerializable(typeof(WeatherReport))]
    internal partial class WeatherReportJsonContext : JsonSerializerContext
    {
    }
}
