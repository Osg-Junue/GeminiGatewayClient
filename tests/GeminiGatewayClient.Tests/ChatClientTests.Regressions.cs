using System.Text.Json;
using System.Text.Json.Nodes;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Tests;

public partial class ChatClientTests
{
    private static GeminiClient CreateClient(HttpClient httpClient, string model = DefaultModel) =>
        new(new GeminiClientOptions
        {
            Model = model,
            ApiKey = "key"
        }, httpClient);

    private static FunctionCallContent SimulatePersistedCall(FunctionCallContent call)
    {
        // A persisted/deserialized chat history loses RawRepresentation; AdditionalProperties values come back as JsonElement.
        var properties = new AdditionalPropertiesDictionary();
        if (call.AdditionalProperties is { } source)
        {
            foreach (var (key, value) in source)
            {
                properties[key] = JsonSerializer.SerializeToElement(value);
            }
        }

        return new FunctionCallContent(call.CallId, call.Name, call.Arguments)
        {
            AdditionalProperties = properties
        };
    }

    [Fact]
    public async Task IChatClient_Streaming_ReportsCumulativeUsageExactlyOnce()
    {
        const string sse = """
            data: {"responseId":"u1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"A"}]}}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":1,"totalTokenCount":4}}

            data: {"responseId":"u1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"B"}]}}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5}}

            data: {"responseId":"u1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"C"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":3,"totalTokenCount":6}}

            """;

        using var handler = new RecordingHandler(sse, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hi")]))
        {
            updates.Add(update);
        }

        var usageContents = updates.SelectMany(u => u.Contents).OfType<UsageContent>().ToList();
        var usage = Assert.Single(usageContents).Details;
        Assert.Equal(3, usage.InputTokenCount);
        Assert.Equal(3, usage.OutputTokenCount);
        Assert.Equal(6, usage.TotalTokenCount);

        var aggregated = updates.ToChatResponse();
        Assert.Equal("ABC", aggregated.Text);
        Assert.Equal(3, aggregated.Usage?.OutputTokenCount);
    }

    [Fact]
    public void IChatClient_Streaming_ValidatesArgumentsEagerly()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        Assert.Throws<ArgumentNullException>(() => { _ = chatClient.GetStreamingResponseAsync((IEnumerable<ChatMessage>)null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = geminiClient.StreamGenerateContentAsync((GenerateContentRequest)null!); });
        Assert.Throws<ArgumentException>(() => { _ = geminiClient.StreamGenerateContentAsync(" "); });

        var invalid = new GenerateContentRequest
        {
            Contents = [Content.FromText("x")],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new ThinkingConfig { ThinkingBudget = 128, ThinkingLevel = ThinkingLevel.Low }
            }
        };
        Assert.Throws<ArgumentException>(() => { _ = geminiClient.StreamGenerateContentAsync(invalid); });
        Assert.Null(handler.LastRequest); // nothing was sent
    }

    [Fact]
    public async Task IChatClient_FunctionCallThoughtSignature_SurvivesLossOfRawRepresentation()
    {
        const string json = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "functionCall": { "id": "call_a", "name": "get_weather", "args": { "city": "Tokyo" } },
                        "thoughtSignature": "sig-fc"
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(json);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var first = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Weather?")]);
        var call = Assert.Single(first.Messages[0].Contents.OfType<FunctionCallContent>());
        var persisted = SimulatePersistedCall(call);
        Assert.Null(persisted.RawRepresentation);

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Weather?"),
            new ChatMessage(ChatRole.Assistant, [persisted]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_a", new Dictionary<string, object?> { ["tempC"] = 20 })])
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var modelPart = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts")[0];
        Assert.Equal("sig-fc", modelPart.GetProperty("thoughtSignature").GetString());
        Assert.Equal("call_a", modelPart.GetProperty("functionCall").GetProperty("id").GetString());
    }

    [Fact]
    public async Task IChatClient_SynthesizedCallIds_AreNeverSentBackToTheServer()
    {
        const string json = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [ { "functionCall": { "name": "get_weather", "args": { "city": "Tokyo" } } } ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(json);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var first = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Weather?")]);
        var call = Assert.Single(first.Messages[0].Contents.OfType<FunctionCallContent>());
        var persisted = SimulatePersistedCall(call);

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Weather?"),
            new ChatMessage(ChatRole.Assistant, [persisted]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, new Dictionary<string, object?> { ["tempC"] = 20 })])
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var contents = sentDoc.RootElement.GetProperty("contents");
        Assert.False(contents[1].GetProperty("parts")[0].GetProperty("functionCall").TryGetProperty("id", out _));
        Assert.False(contents[2].GetProperty("parts")[0].GetProperty("functionResponse").TryGetProperty("id", out _));
        Assert.Equal("get_weather", contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("name").GetString());
    }

    [Fact]
    public async Task IChatClient_TextWithThoughtSignature_IsNotDuplicatedWhenReplayed()
    {
        const string json = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": { "role": "model", "parts": [ { "text": "Hello", "thoughtSignature": "sig-text" } ] },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(json);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var first = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")]);
        var singleTextContent = Assert.Single(first.Messages[0].Contents);
        Assert.IsType<TextContent>(singleTextContent);

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Hi"),
            first.Messages[0],
            new ChatMessage(ChatRole.User, "Next")
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var assistantParts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(1, assistantParts.GetArrayLength());
        Assert.Equal("Hello", assistantParts[0].GetProperty("text").GetString());
        Assert.Equal("sig-text", assistantParts[0].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task IChatClient_ReasoningEffortNone_UsesThinkingBudgetForGemini25()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient); // default model: gemini-2.5-flash
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } });

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var thinkingConfig = sentDoc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.Equal(0, thinkingConfig.GetProperty("thinkingBudget").GetInt32());
        Assert.False(thinkingConfig.TryGetProperty("thinkingLevel", out _));
    }

    [Fact]
    public async Task IChatClient_ReasoningEffortNone_ClampsToLowForGemini3Pro()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            new ChatOptions
            {
                ModelId = "gemini-3-pro-preview",
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None }
            });

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var thinkingConfig = sentDoc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.Equal("LOW", thinkingConfig.GetProperty("thinkingLevel").GetString());
        Assert.False(thinkingConfig.TryGetProperty("thinkingBudget", out _));
    }

    [Fact]
    public async Task IChatClient_UnsupportedCustomTool_ThrowsNotSupportedException()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await Assert.ThrowsAsync<NotSupportedException>(() => chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Search")],
            new ChatOptions { Tools = [new CustomUnsupportedTool()] }));
    }

    [Fact]
    public async Task IChatClient_HostedWebSearchAndCodeInterpreterTools_MapToGoogleSearchAndCodeExecution()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Search and compute")],
            new ChatOptions
            {
                Tools = [new HostedWebSearchTool(), new HostedCodeInterpreterTool()]
            });

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var tools = sentDoc.RootElement.GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        Assert.True(tools[0].TryGetProperty("googleSearch", out _));
        Assert.False(tools[0].TryGetProperty("functionDeclarations", out _));
        Assert.True(tools[1].TryGetProperty("codeExecution", out _));
        Assert.False(tools[1].TryGetProperty("functionDeclarations", out _));
    }

    [Fact]
    public async Task IChatClient_MultipleConsecutiveToolMessages_AreMergedIntoSingleUserContentTurn()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Compare Tokyo and Osaka weather"),
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "Tokyo" }),
                new FunctionCallContent("call_2", "get_weather", new Dictionary<string, object?> { ["city"] = "Osaka" })
            ]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", new Dictionary<string, object?> { ["tempC"] = 20 })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_2", new Dictionary<string, object?> { ["tempC"] = 23 })])
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var contents = sentDoc.RootElement.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());

        var mergedToolTurn = contents[2];
        Assert.Equal("user", mergedToolTurn.GetProperty("role").GetString());
        var parts = mergedToolTurn.GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("call_1", parts[0].GetProperty("functionResponse").GetProperty("id").GetString());
        Assert.Equal("call_2", parts[1].GetProperty("functionResponse").GetProperty("id").GetString());
    }

    [Fact]
    public async Task IChatClient_DataContentThoughtSignature_SurvivesLossOfRawRepresentation()
    {
        const string json = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "inlineData": { "mimeType": "image/png", "data": "AQIDBA==" },
                        "thoughtSignature": "sig-image-part"
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(json);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var first = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Draw")]);
        var imageContent = Assert.Single(first.Messages[0].Contents.OfType<DataContent>());

        var persistedImage = new DataContent(imageContent.Data, imageContent.MediaType)
        {
            AdditionalProperties = imageContent.AdditionalProperties
        };
        Assert.Null(persistedImage.RawRepresentation);

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Draw"),
            new ChatMessage(ChatRole.Assistant, [persistedImage]),
            new ChatMessage(ChatRole.User, "Make it blue")
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var modelPart = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts")[0];
        Assert.Equal("sig-image-part", modelPart.GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task IChatClient_ReasoningEffort_OnGemini20_DoesNotSendUnsupportedThinkingLevelOrBudget()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient, model: "gemini-2.0-flash");
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium }
            });

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        Assert.False(sentDoc.RootElement.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
    }

    private sealed class CustomUnsupportedTool : AITool;

    [Fact]
    public async Task IChatClient_JsonNodeAndBinaryToolResults_SerializeAsJson()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Run"),
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("c1", "node_tool"),
                new FunctionCallContent("c2", "binary_tool")
            ]),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("c1", JsonNode.Parse("""{"a":1,"nested":{"b":[1,2]}}""")),
                new FunctionResultContent("c2", new Dictionary<string, object?> { ["data"] = new byte[] { 1, 2 } })
            ])
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var toolParts = sentDoc.RootElement.GetProperty("contents")[2].GetProperty("parts");
        var nodeResponse = toolParts[0].GetProperty("functionResponse").GetProperty("response");
        Assert.Equal(1, nodeResponse.GetProperty("a").GetInt32());
        Assert.Equal(2, nodeResponse.GetProperty("nested").GetProperty("b").GetArrayLength());
        Assert.Equal("AQI=", toolParts[1].GetProperty("functionResponse").GetProperty("response").GetProperty("data").GetString());
    }

    [Fact]
    public async Task IChatClient_DisposingAdapter_DoesNotDisposeUnderlyingGeminiClient()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);

        using (IChatClient chatClient = geminiClient.AsIChatClient())
        {
            await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Via adapter")]);
        }

        var response = await geminiClient.GenerateContentAsync("Direct after adapter dispose");
        Assert.Equal("Hello from Gemini!", response.Text);
    }

    [Fact]
    public async Task IChatClient_ExecutableCodeWithThoughtSignature_PreservesCodePartOnRoundTrip()
    {
        const string codeResponseJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "executableCode": {
                          "language": "PYTHON",
                          "code": "print(6 * 7)"
                        },
                        "thoughtSignature": "sig-exec-code"
                      },
                      {
                        "codeExecutionResult": {
                          "outcome": "OUTCOME_OK",
                          "output": "42\n"
                        }
                      },
                      {
                        "text": "The answer is 42."
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(codeResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var first = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Compute 6*7")]);
        Assert.Equal(3, first.Messages[0].Contents.Count);

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Compute 6*7"),
            first.Messages[0],
            new ChatMessage(ChatRole.User, "Now multiply by 2")
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var modelParts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(3, modelParts.GetArrayLength());
        Assert.Equal("print(6 * 7)", modelParts[0].GetProperty("executableCode").GetProperty("code").GetString());
        Assert.Equal("sig-exec-code", modelParts[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("42\n", modelParts[1].GetProperty("codeExecutionResult").GetProperty("output").GetString());
    }

    [Fact]
    public async Task IChatClient_EmptyStringTextWithThoughtSignature_MapsToReasoningContentAndSurvivesPersistence()
    {
        const string sse = """
            data: {"responseId":"r1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Answer"}]}}]}

            data: {"responseId":"r1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"","thoughtSignature":"sig-empty-text"}]},"finishReason":"STOP"}]}

            """;

        using var handler = new RecordingHandler(sse, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hi")]))
        {
            updates.Add(update);
        }

        // Second chunk must contain a single TextReasoningContent with ProtectedData, not an empty TextContent
        var secondUpdateContent = Assert.Single(updates[1].Contents);
        var reasoning = Assert.IsType<TextReasoningContent>(secondUpdateContent);
        Assert.Equal(string.Empty, reasoning.Text);
        Assert.Equal("sig-empty-text", reasoning.ProtectedData);

        // Simulate persisted chat history (RawRepresentation stripped)
        var persistedAssistant = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("Answer"),
            new TextReasoningContent(string.Empty) { ProtectedData = "sig-empty-text" }
        ]);

        using var nextHandler = new RecordingHandler(OkResponseJson);
        using var nextHttpClient = new HttpClient(nextHandler);
        using var nextGeminiClient = CreateClient(nextHttpClient);
        IChatClient nextChatClient = nextGeminiClient.AsIChatClient();

        await nextChatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Hi"),
            persistedAssistant,
            new ChatMessage(ChatRole.User, "Follow-up")
        ]);

        Assert.NotNull(nextHandler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(nextHandler.LastRequestBody);
        var parts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("Answer", parts[0].GetProperty("text").GetString());
        Assert.False(parts[0].TryGetProperty("thoughtSignature", out _));
        Assert.Equal("sig-empty-text", parts[1].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task IChatClient_ExecutableCodeAndResult_SurviveLossOfRawRepresentation()
    {
        const string codeResponseJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "executableCode": {
                          "language": "PYTHON",
                          "code": "print(6 * 7)"
                        },
                        "thoughtSignature": "sig-exec-persisted"
                      },
                      {
                        "codeExecutionResult": {
                          "outcome": "OUTCOME_OK",
                          "output": "42\n"
                        }
                      },
                      {
                        "text": "The answer is 42."
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        using var handler = new RecordingHandler(codeResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var first = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "Compute 6*7")]);

        // Strip RawRepresentation to simulate JSON persistence of ChatMessage history
        var persistedContents = first.Messages[0].Contents.Select(c => c switch
        {
            TextContent tc => (AIContent)new TextContent(tc.Text) { AdditionalProperties = tc.AdditionalProperties },
            _ => new AIContent { AdditionalProperties = c.AdditionalProperties }
        }).ToList();

        Assert.All(persistedContents, c => Assert.Null(c.RawRepresentation));

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Compute 6*7"),
            new ChatMessage(ChatRole.Assistant, persistedContents),
            new ChatMessage(ChatRole.User, "Now multiply by 2")
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var modelParts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(3, modelParts.GetArrayLength());
        Assert.Equal("PYTHON", modelParts[0].GetProperty("executableCode").GetProperty("language").GetString());
        Assert.Equal("print(6 * 7)", modelParts[0].GetProperty("executableCode").GetProperty("code").GetString());
        Assert.Equal("sig-exec-persisted", modelParts[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("OUTCOME_OK", modelParts[1].GetProperty("codeExecutionResult").GetProperty("outcome").GetString());
        Assert.Equal("42\n", modelParts[1].GetProperty("codeExecutionResult").GetProperty("output").GetString());
    }

    [Fact]
    public async Task IChatClient_AsIChatClient_InheritsToolSerializerOptionsFromGeminiClientOptions()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key",
            ToolSerializerOptions = new JsonSerializerOptions
            {
                TypeInfoResolver = WeatherReportJsonContext.Default
            }
        }, httpClient);

        // Call AsIChatClient() without passing toolSerializerOptions explicitly
        IChatClient chatClient = geminiClient.AsIChatClient();

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
    }

    [Fact]
    public async Task IChatClient_ToolSerializerOptionsWithNullResolver_ThrowsActionableMessageForCustomType()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient(toolSerializerOptions: new JsonSerializerOptions { TypeInfoResolver = null });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Weather?"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "get_weather")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new WeatherReport("Tokyo", 21.5, DateTime.UtcNow))])
        ]));

        Assert.Contains("JsonSerializerContext", ex.Message);
    }

    [Fact]
    public async Task IChatClient_StreamingPureEmptyTextPart_IsOmittedAndNotSentOnWire()
    {
        const string sse = """
            data: {"responseId":"r-empty","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Checking weather...","thought":true}]}}]}

            data: {"responseId":"r-empty","candidates":[{"index":0,"content":{"role":"model","parts":[{"functionCall":{"id":"call_1","name":"get_weather","args":{"city":"Tokyo"}},"thoughtSignature":"sig-fc-1"}]}}]}

            data: {"responseId":"r-empty","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":""}]},"finishReason":"STOP"}]}

            """;

        using var handler = new RecordingHandler(sse, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Tokyo weather?")]))
        {
            updates.Add(update);
        }

        // Pure empty {"text": ""} part without thoughtSignature must not produce an empty TextContent("")
        Assert.Empty(updates.SelectMany(u => u.Contents).OfType<TextContent>());

        var aggregated = updates.ToChatResponse();
        using var nextHandler = new RecordingHandler(OkResponseJson);
        using var nextHttpClient = new HttpClient(nextHandler);
        using var nextGeminiClient = CreateClient(nextHttpClient);
        IChatClient nextChatClient = nextGeminiClient.AsIChatClient();

        await nextChatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Tokyo weather?"),
            .. aggregated.Messages,
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", new Dictionary<string, object?> { ["tempC"] = 20 })])
        ]);

        Assert.NotNull(nextHandler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(nextHandler.LastRequestBody);
        var assistantParts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(2, assistantParts.GetArrayLength());
        Assert.True(assistantParts[0].GetProperty("thought").GetBoolean());
        Assert.Equal("get_weather", assistantParts[1].GetProperty("functionCall").GetProperty("name").GetString());
        Assert.Equal("sig-fc-1", assistantParts[1].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task IChatClient_MultiChunkTextStreaming_PreservesTrailingThoughtSignatureAfterToChatResponse()
    {
        const string multiChunkSse = """
            data: {"responseId":"r-multi","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Hello "}]}}]}

            data: {"responseId":"r-multi","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"world!","thoughtSignature":"sig-multi-chunk"}]},"finishReason":"STOP"}]}

            """;

        using var handler = new RecordingHandler(multiChunkSse, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Greet")]))
        {
            updates.Add(update);
        }

        var aggregated = updates.ToChatResponse();
        Assert.Equal("Hello world!", aggregated.Text);

        using var nextHandler = new RecordingHandler(OkResponseJson);
        using var nextHttpClient = new HttpClient(nextHandler);
        using var nextGeminiClient = CreateClient(nextHttpClient);
        IChatClient nextChatClient = nextGeminiClient.AsIChatClient();

        await nextChatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Greet"),
            .. aggregated.Messages,
            new ChatMessage(ChatRole.User, "Follow-up")
        ]);

        Assert.NotNull(nextHandler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(nextHandler.LastRequestBody);
        var assistantParts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(1, assistantParts.GetArrayLength());
        Assert.Equal("Hello world!", assistantParts[0].GetProperty("text").GetString());
        Assert.Equal("sig-multi-chunk", assistantParts[0].GetProperty("thoughtSignature").GetString());

        // Single-chunk streaming with text + thoughtSignature must deduplicate the companion and send only 1 wire part
        const string singleChunkSse = """
            data: {"responseId":"r-single","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Single chunk","thoughtSignature":"sig-single-chunk"}]},"finishReason":"STOP"}]}

            """;

        using var singleHandler = new RecordingHandler(singleChunkSse, contentType: "text/event-stream");
        using var singleHttpClient = new HttpClient(singleHandler);
        using var singleGeminiClient = CreateClient(singleHttpClient);
        IChatClient singleChatClient = singleGeminiClient.AsIChatClient();

        var singleUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in singleChatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Greet")]))
        {
            singleUpdates.Add(update);
        }

        var singleAggregated = singleUpdates.ToChatResponse();
        await nextChatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Greet"),
            .. singleAggregated.Messages,
            new ChatMessage(ChatRole.User, "Follow-up")
        ]);

        Assert.NotNull(nextHandler.LastRequestBody);
        using var singleSentDoc = JsonDocument.Parse(nextHandler.LastRequestBody);
        var singleAssistantParts = singleSentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(1, singleAssistantParts.GetArrayLength());
        Assert.Equal("Single chunk", singleAssistantParts[0].GetProperty("text").GetString());
        Assert.Equal("sig-single-chunk", singleAssistantParts[0].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task IChatClient_ReusedCallIdAcrossTurns_ResolvesChronologicallyToCorrectFunctionName()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Weather in Tokyo?"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "Tokyo" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", new Dictionary<string, object?> { ["tempC"] = 22 })]),
            new ChatMessage(ChatRole.Assistant, "It is 22C in Tokyo."),
            new ChatMessage(ChatRole.User, "Now check stock price for GOOG."),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "get_stock_price", new Dictionary<string, object?> { ["ticker"] = "GOOG" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", new Dictionary<string, object?> { ["price"] = 185.5 })])
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var contents = sentDoc.RootElement.GetProperty("contents");

        // Turn 1 tool response (contents[2]) must resolve to get_weather, not be overwritten by Turn 2's get_stock_price
        var turn1Response = contents[2].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("get_weather", turn1Response.GetProperty("name").GetString());
        Assert.Equal("call_1", turn1Response.GetProperty("id").GetString());

        // Turn 2 tool response (contents[6]) must resolve to get_stock_price
        var turn2Response = contents[6].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("get_stock_price", turn2Response.GetProperty("name").GetString());
        Assert.Equal("call_1", turn2Response.GetProperty("id").GetString());
    }

    [Fact]
    public async Task IChatClient_MutatedFunctionCallDataAndUriContentsWithRawRepresentation_ReflectUpdatedValues()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var rawCallPart = new Part
        {
            ThoughtSignature = "sig-fc-raw",
            FunctionCall = new FunctionCall
            {
                Id = "call_orig",
                Name = "lookup_user",
                Args = JsonDocument.Parse("""{"secret":"raw-secret"}""").RootElement.Clone()
            }
        };

        var mutatedCall = new FunctionCallContent(
            "call_updated",
            "lookup_user",
            new Dictionary<string, object?> { ["secret"] = "[REDACTED]" })
        {
            RawRepresentation = rawCallPart
        };

        var rawDataPart = new Part
        {
            ThoughtSignature = "sig-data-raw",
            InlineData = new Blob { MimeType = "image/png", Data = [1, 2, 3] }
        };

        var mutatedData = new DataContent(new byte[] { 9, 8, 7 }, "image/jpeg")
        {
            RawRepresentation = rawDataPart
        };

        var rawUriPart = new Part
        {
            ThoughtSignature = "sig-uri-raw",
            FileData = new FileData { FileUri = "https://example.com/doc", MimeType = "text/plain" }
        };

        var mutatedUri = new UriContent(new Uri("https://example.com/doc"), "application/pdf")
        {
            RawRepresentation = rawUriPart
        };

        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, [mutatedData, mutatedUri]),
            new ChatMessage(ChatRole.Assistant, [mutatedCall]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_updated", new Dictionary<string, object?> { ["ok"] = true })])
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var contents = sentDoc.RootElement.GetProperty("contents");

        var userParts = contents[0].GetProperty("parts");
        Assert.Equal("image/jpeg", userParts[0].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(new byte[] { 9, 8, 7 }), userParts[0].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal("sig-data-raw", userParts[0].GetProperty("thoughtSignature").GetString());

        Assert.Equal("application/pdf", userParts[1].GetProperty("fileData").GetProperty("mimeType").GetString());
        Assert.Equal("sig-uri-raw", userParts[1].GetProperty("thoughtSignature").GetString());

        var assistantPart = contents[1].GetProperty("parts")[0];
        Assert.Equal("sig-fc-raw", assistantPart.GetProperty("thoughtSignature").GetString());
        Assert.Equal("call_updated", assistantPart.GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("[REDACTED]", assistantPart.GetProperty("functionCall").GetProperty("args").GetProperty("secret").GetString());
    }

    [Fact]
    public async Task IChatClient_AIFunctionDeclaration_MapsReturnJsonSchemaToResponseJsonSchema()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var typedReturnTool = AIFunctionFactory.Create(
            (string city) => new WeatherReport(city, 20.0, DateTime.UtcNow),
            "get_weather_report",
            "Returns a typed weather report");
        var untypedObjectTool = AIFunctionFactory.Create(
            (string query) => (object)new { query },
            "untyped_tool",
            "Returns an untyped object");
        var voidTool = AIFunctionFactory.Create(
            (string message) => { },
            "log_message",
            "Logs a message");

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Check")],
            new ChatOptions
            {
                Tools = [typedReturnTool, untypedObjectTool, voidTool]
            });

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var declarations = sentDoc.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations");
        Assert.Equal(3, declarations.GetArrayLength());

        Assert.True(declarations[0].TryGetProperty("responseJsonSchema", out var responseSchema));
        Assert.Equal("object", responseSchema.GetProperty("type").GetString());
        Assert.True(responseSchema.GetProperty("properties").TryGetProperty("city", out _));

        // object return (empty schema {}) and void return (null schema) must omit responseJsonSchema
        Assert.False(declarations[1].TryGetProperty("responseJsonSchema", out _));
        Assert.False(declarations[2].TryGetProperty("responseJsonSchema", out _));
    }
}
