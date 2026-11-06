using System.Net;
using System.Text;
using System.Text.Json;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Http;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GeminiGatewayClient.Tests;

public partial class ChatClientTests
{
    [Fact]
    public async Task Stream_SseKeepAliveOrDoneOnly_CompletesCleanlyWithZeroChunks()
    {
        const string keepAliveAndDone = ": keep-alive\n\ndata: [DONE]\n\n";
        using var handler = new RecordingHandler(keepAliveAndDone, HttpStatusCode.OK, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var count = 0;
        await foreach (var _ in client.StreamGenerateContentAsync("Hi"))
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IChatClient_MultiChunkStreamingTextWithTrailingThoughtSignature_KeepsItsPartBoundaryOnNextTurn(bool signatureInSeparateEmptyTextChunk)
    {
        var multiChunkSse = signatureInSeparateEmptyTextChunk
            ? """
              data: {"responseId":"r-multi","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Thinking...","thought":true,"thoughtSignature":"sig-thought-1"}]}}]}

              data: {"responseId":"r-multi","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Hello "}]}}]}

              data: {"responseId":"r-multi","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"world!"}]}}]}

              data: {"responseId":"r-multi","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"","thoughtSignature":"sig-text-final"}]},"finishReason":"STOP"}]}

              """
            : """
              data: {"responseId":"r-multi","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Thinking...","thought":true,"thoughtSignature":"sig-thought-1"}]}}]}

              data: {"responseId":"r-multi","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Hello "}]}}]}

              data: {"responseId":"r-multi","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"world!","thoughtSignature":"sig-text-final"}]},"finishReason":"STOP"}]}

              """;

        using var handler = new RecordingHandler(multiChunkSse, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Greet me")]))
        {
            updates.Add(update);
        }

        var aggregated = updates.ToChatResponse();
        Assert.Equal("Hello world!", aggregated.Text);

        using var nextTurnHandler = new RecordingHandler(OkResponseJson);
        using var nextTurnHttp = new HttpClient(nextTurnHandler);
        using var nextTurnGemini = CreateClient(nextTurnHttp);
        IChatClient nextTurnChat = nextTurnGemini.AsIChatClient();

        await nextTurnChat.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Greet me"),
            .. aggregated.Messages,
            new ChatMessage(ChatRole.User, "Follow-up question")
        ]);

        Assert.NotNull(nextTurnHandler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(nextTurnHandler.LastRequestBody);
        var assistantParts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");

        // A signature on its own part stays there: Gemini forbids merging a signed part with an unsigned one.
        Assert.Equal(signatureInSeparateEmptyTextChunk ? 3 : 2, assistantParts.GetArrayLength());

        Assert.True(assistantParts[0].GetProperty("thought").GetBoolean());
        Assert.Equal("Thinking...", assistantParts[0].GetProperty("text").GetString());
        Assert.Equal("sig-thought-1", assistantParts[0].GetProperty("thoughtSignature").GetString());

        Assert.False(assistantParts[1].TryGetProperty("thought", out _));
        Assert.Equal("Hello world!", assistantParts[1].GetProperty("text").GetString());
        Assert.Equal("sig-text-final", assistantParts[assistantParts.GetArrayLength() - 1].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task EmbedContentAsync_WithNullRequestModel_PopulatesResolvedModelInWireBodyWithoutMutatingCallerRequest()
    {
        const string embedJson = """{ "embedding": { "values": [0.25, 0.75] } }""";
        using var handler = new RecordingHandler(embedJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            EmbeddingModel = "gemini-embedding-001",
            ApiKey = "test-key"
        }, httpClient);

        var request = EmbedContentRequest.FromText("Embed this text");
        Assert.Null(request.Model);

        var response = await client.EmbedContentAsync(request);
        Assert.Null(request.Model);
        Assert.Equal([0.25f, 0.75f], response.Embedding!.Values);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal("models/gemini-embedding-001", sentDoc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void ChatRequestMapper_MergesConsecutiveToolMessages_ButSeparatesToolAndUserMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Run two tools and then read my follow-up"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "tool_a"),
                new FunctionCallContent("call-2", "tool_b")
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result-a")]),
            new(ChatRole.Tool, [new FunctionResultContent("call-2", "result-b")]),
            new(ChatRole.User, "Follow-up user instruction after tool results")
        };

        using var geminiClient = new GeminiClient("key", DefaultModel);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var request = ChatRequestMapper.ToRequest(
            messages,
            options: null,
            chatClient,
            DefaultModel,
            AIJsonUtilities.DefaultOptions);

        Assert.Equal(4, request.Contents.Count);
        Assert.Equal(Role.User, request.Contents[0].Role);
        Assert.Equal("Run two tools and then read my follow-up", Assert.Single(request.Contents[0].Parts).Text);

        Assert.Equal(Role.Model, request.Contents[1].Role);
        Assert.Equal(2, request.Contents[1].Parts.Count);

        // Consecutive ChatRole.Tool messages are merged into a single Role.User turn containing only functionResponse parts
        Assert.Equal(Role.User, request.Contents[2].Role);
        Assert.Equal(2, request.Contents[2].Parts.Count);
        Assert.All(request.Contents[2].Parts, p => Assert.NotNull(p.FunctionResponse));

        // Subsequent ChatRole.User message starts a separate Role.User turn rather than mixing into the functionResponse turn
        Assert.Equal(Role.User, request.Contents[3].Role);
        var followUpPart = Assert.Single(request.Contents[3].Parts);
        Assert.Equal("Follow-up user instruction after tool results", followUpPart.Text);
        Assert.Null(followUpPart.FunctionResponse);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("models/..")]
    [InlineData("models/.")]
    [InlineData("../../admin/internal")]
    [InlineData("projects/p/locations/../models/gemini-2.5-flash")]
    public async Task Endpoint_ModelWithRelativeDotSegments_ThrowsArgumentException(string maliciousModel)
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);

        Assert.Throws<ArgumentException>(() => new GeminiClient(new GeminiClientOptions
        {
            Model = maliciousModel,
            ApiKey = "key"
        }, httpClient));

        using var client = CreateClient(httpClient);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateContentAsync("Hi", model: maliciousModel));

        IChatClient chatClient = client.AsIChatClient();
        await Assert.ThrowsAsync<ArgumentException>(() => chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hi")],
            new ChatOptions { ModelId = maliciousModel }));
    }

    [Fact]
    public async Task RequestHeaders_NullCharOrControlCharInStaticOrDynamicKey_ThrowsException()
    {
        Assert.Throws<ArgumentException>(() => new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "bad\0key"
        }));

        Assert.Throws<ArgumentException>(() => new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["x-custom"] = "val\u0007bell"
            }
        }));

        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKeyProvider = _ => ValueTask.FromResult("token-with-\0-null")
        }, httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateContentAsync("Hi"));
    }

    [Fact]
    public async Task IChatClient_MultiChunkStreaming_ToChatResponse_ThenPersistedWithoutRawRepresentation_KeepsThoughtSignature()
    {
        const string sse = """
            data: {"responseId":"r-p1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Hello "}]}}]}

            data: {"responseId":"r-p1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"world!"}]}}]}

            data: {"responseId":"r-p1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"","thoughtSignature":"sig-persisted-final","sigExtra":"keep-sig-extra"}]},"finishReason":"STOP"}]}

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

        var aggregated = updates.ToChatResponse();

        // 1. Before persistence (RawRepresentation present on trailing TextReasoningContent): ExtensionData is preserved on merged part
        using var prePersistHandler = new RecordingHandler(OkResponseJson);
        using var prePersistHttp = new HttpClient(prePersistHandler);
        using var prePersistGemini = CreateClient(prePersistHttp);
        IChatClient prePersistChat = prePersistGemini.AsIChatClient();

        await prePersistChat.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi"), aggregated.Messages[0], new ChatMessage(ChatRole.User, "Next")]);
        using (var preDoc = JsonDocument.Parse(prePersistHandler.LastRequestBody!))
        {
            var preParts = preDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
            Assert.Equal(2, preParts.GetArrayLength());
            Assert.Equal("Hello world!", preParts[0].GetProperty("text").GetString());
            Assert.Equal("sig-persisted-final", preParts[1].GetProperty("thoughtSignature").GetString());
            Assert.Equal("keep-sig-extra", preParts[1].GetProperty("sigExtra").GetString());
        }

        // 2. After persistence (RawRepresentation stripped): ThoughtSignature still re-attaches onto the preceding text part
        var persistedContents = new List<AIContent>();
        foreach (var content in aggregated.Messages[0].Contents)
        {
            switch (content)
            {
                case TextContent tc:
                    persistedContents.Add(new TextContent(tc.Text) { AdditionalProperties = tc.AdditionalProperties });
                    break;
                case TextReasoningContent trc:
                    persistedContents.Add(new TextReasoningContent(trc.Text) { ProtectedData = trc.ProtectedData, AdditionalProperties = trc.AdditionalProperties });
                    break;
            }
        }

        var persistedAssistant = new ChatMessage(ChatRole.Assistant, persistedContents);

        using var nextHandler = new RecordingHandler(OkResponseJson);
        using var nextHttp = new HttpClient(nextHandler);
        using var nextGemini = CreateClient(nextHttp);
        IChatClient nextChat = nextGemini.AsIChatClient();

        await nextChat.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi"), persistedAssistant, new ChatMessage(ChatRole.User, "Next")]);

        Assert.NotNull(nextHandler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(nextHandler.LastRequestBody);
        var parts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("Hello world!", parts[0].GetProperty("text").GetString());
        Assert.Equal("sig-persisted-final", parts[1].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task AddGeminiClient_ReplacingIGeminiClientAfterRegistration_IsRespectedByIChatClientAndIEmbeddingGenerator_AndUsesSingletonLifetime()
    {
        // Note: ApiKey is intentionally NOT configured on GeminiClientOptions here to ensure that replacing IGeminiClient
        // with a custom mock/stub after AddGeminiClient does not trigger OptionsValidationException when resolving IChatClient.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddGeminiClient(options =>
        {
            options.Model = DefaultModel;
        });

        var customGemini = new StubGeminiClient();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IGeminiClient>(services, customGemini);

        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var chat1 = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IChatClient>(provider);
        var chat2 = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IChatClient>(provider);
        Assert.Same(chat1, chat2);

        var embed1 = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(provider);
        var embed2 = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(provider);
        Assert.Same(embed1, embed2);

        var chatResponse = await chat1.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);
        Assert.Equal("from-custom-stub", chatResponse.Text);
        Assert.Equal(1, customGemini.GenerateCalls);

        var embeddings = await embed1.GenerateAsync(["vec"]);
        Assert.Single(embeddings);
        Assert.Equal(1, customGemini.EmbedCalls);
    }

    [Fact]
    public void LenientStringEnumConverter_NumericTokens_MapToOfficialProtobufValues_AndUnmappedZeroMapsToUnknown()
    {
        var opts = GeminiGatewayClient.Serialization.GeminiJsonContext.Default.Options;

        Assert.Equal(FinishReason.Unspecified, JsonSerializer.Deserialize<FinishReason>("0", opts));
        Assert.Equal(FinishReason.Stop, JsonSerializer.Deserialize<FinishReason>("1", opts));
        Assert.Equal(FinishReason.MaxTokens, JsonSerializer.Deserialize<FinishReason>("2", opts));
        Assert.Equal(FinishReason.MalformedFunctionCall, JsonSerializer.Deserialize<FinishReason>("10", opts));
        Assert.Equal(FinishReason.ImageSafety, JsonSerializer.Deserialize<FinishReason>("11", opts));
        Assert.Equal(FinishReason.UnexpectedToolCall, JsonSerializer.Deserialize<FinishReason>("12", opts));
        Assert.Equal(FinishReason.Unknown, JsonSerializer.Deserialize<FinishReason>("-1", opts));
        Assert.Equal(FinishReason.Unknown, JsonSerializer.Deserialize<FinishReason>("999", opts));

        Assert.Equal(Role.Unknown, JsonSerializer.Deserialize<Role>("0", opts));
        Assert.Equal(Role.User, JsonSerializer.Deserialize<Role>("1", opts));
        Assert.Equal(Role.Model, JsonSerializer.Deserialize<Role>("2", opts));

        Assert.Equal(FunctionCallingMode.Unknown, JsonSerializer.Deserialize<FunctionCallingMode>("0", opts));
        Assert.Equal(FunctionCallingMode.Auto, JsonSerializer.Deserialize<FunctionCallingMode>("1", opts));
        Assert.Equal(FunctionCallingMode.Any, JsonSerializer.Deserialize<FunctionCallingMode>("2", opts));
        Assert.Equal(FunctionCallingMode.None, JsonSerializer.Deserialize<FunctionCallingMode>("3", opts));
        Assert.Equal(FunctionCallingMode.Validated, JsonSerializer.Deserialize<FunctionCallingMode>("4", opts));
    }

    [Fact]
    public void Validate_OutboundRequests_RejectsUnknownEnumsAndInvalidJsonElements_WhileAllowingCodeExecutionUnknowns()
    {
        // Role.Unknown rejected
        var badRoleReq = new GenerateContentRequest
        {
            Contents = [new Content { Role = Role.Unknown, Parts = [Part.FromText("Hi")] }]
        };
        Assert.Throws<ArgumentException>(() => badRoleReq.Validate());

        // SchemaType.Unknown rejected (including nested)
        var badSchemaReq = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            GenerationConfig = new GenerationConfig
            {
                ResponseSchema = new Schema
                {
                    Type = SchemaType.Object,
                    Properties = new Dictionary<string, Schema>
                    {
                        ["nested"] = new Schema { Type = SchemaType.Unknown }
                    }
                }
            }
        };
        Assert.Throws<ArgumentException>(() => badSchemaReq.Validate());

        // Schema.Default with Undefined JsonElement rejected
        var badSchemaDefaultReq = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            GenerationConfig = new GenerationConfig
            {
                ResponseSchema = new Schema
                {
                    Type = SchemaType.String,
                    Default = default(JsonElement)
                }
            }
        };
        Assert.Throws<ArgumentException>(() => badSchemaDefaultReq.Validate());

        // FunctionResponse non-Object rejected in factory and Validate
        Assert.Throws<ArgumentException>(() => Part.FromFunctionResponse("fn", default));
        using var arrayDoc = JsonDocument.Parse("[1, 2]");
        Assert.Throws<ArgumentException>(() => Part.FromFunctionResponse("fn", arrayDoc.RootElement));
        var badFuncRespPart = new Part
        {
            FunctionResponse = new FunctionResponse { Name = "fn", Response = arrayDoc.RootElement }
        };
        Assert.Throws<ArgumentException>(() => new GenerateContentRequest
        {
            Contents = [new Content { Role = Role.User, Parts = [badFuncRespPart] }]
        }.Validate());

        // FunctionCall.Args with default(JsonElement) rejected
        var badFuncCallPart = new Part
        {
            FunctionCall = new FunctionCall { Name = "fn", Args = default(JsonElement) }
        };
        Assert.Throws<ArgumentException>(() => new GenerateContentRequest
        {
            Contents = [new Content { Role = Role.Model, Parts = [badFuncCallPart] }]
        }.Validate());

        // FunctionDeclaration.ParametersJsonSchema / ResponseJsonSchema / GenerationConfig.ResponseJsonSchema with default(JsonElement) rejected
        Assert.Throws<ArgumentException>(() => new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            Tools = [new Tool { FunctionDeclarations = [new FunctionDeclaration { Name = "fn", ParametersJsonSchema = default(JsonElement) }] }]
        }.Validate());

        Assert.Throws<ArgumentException>(() => new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            GenerationConfig = new GenerationConfig { ResponseJsonSchema = default(JsonElement) }
        }.Validate());

        // CodeExecutionLanguage.Unknown and CodeExecutionOutcome.Unknown MUST be allowed for multi-turn forward compatibility
        var codeExecReq = new GenerateContentRequest
        {
            Contents =
            [
                Content.FromText("Compute"),
                new Content
                {
                    Role = Role.Model,
                    Parts =
                    [
                        new Part { ExecutableCode = new ExecutableCode { Language = CodeExecutionLanguage.Unknown, Code = "1+1" } },
                        new Part { CodeExecutionResult = new CodeExecutionResult { Outcome = CodeExecutionOutcome.Unknown, Output = "2" } }
                    ]
                }
            ]
        };
        codeExecReq.Validate();
    }

    [Fact]
    public async Task GeminiEmbeddingGenerator_RawRepresentationFactoryOutputDimensionality_TakesPrecedenceOverDefaultModelDimensions()
    {
        const string singleEmbedJson = """{"embeddings":[{"values":[0.1,0.2]}]}""";
        using var singleHandler = new RecordingHandler(singleEmbedJson);
        using var singleHttp = new HttpClient(singleHandler);
        using var singleClient = CreateClient(singleHttp);
        var generator = singleClient.AsIEmbeddingGenerator(defaultModelDimensions: 768);

        await generator.GenerateAsync(["hello"], new EmbeddingGenerationOptions
        {
            RawRepresentationFactory = _ => new EmbedContentRequest { OutputDimensionality = 256 }
        });

        Assert.NotNull(singleHandler.LastRequestBody);
        using var singleDoc = JsonDocument.Parse(singleHandler.LastRequestBody);
        Assert.Equal(256, singleDoc.RootElement.GetProperty("requests")[0].GetProperty("outputDimensionality").GetInt32());

        const string batchEmbedJson = """{"embeddings":[{"values":[0.1]},{"values":[0.2]}]}""";
        using var batchHandler = new RecordingHandler(batchEmbedJson);
        using var batchHttp = new HttpClient(batchHandler);
        using var batchClient = CreateClient(batchHttp);
        var batchGenerator = batchClient.AsIEmbeddingGenerator(defaultModelDimensions: 768);

        await batchGenerator.GenerateAsync(["a", "b"], new EmbeddingGenerationOptions
        {
            RawRepresentationFactory = _ => new EmbedContentRequest { OutputDimensionality = 512 }
        });

        Assert.NotNull(batchHandler.LastRequestBody);
        using var batchDoc = JsonDocument.Parse(batchHandler.LastRequestBody);
        var batchRequests = batchDoc.RootElement.GetProperty("requests");
        Assert.Equal(512, batchRequests[0].GetProperty("outputDimensionality").GetInt32());
        Assert.Equal(512, batchRequests[1].GetProperty("outputDimensionality").GetInt32());
    }

    [Fact]
    public async Task IChatClient_RawRepresentationFactoryWithTools_MergesWithChatOptionsTools()
    {
        var aiFunction = AIFunctionFactory.Create((string city) => city, "get_weather", "Gets weather");

        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Weather?")],
            new ChatOptions
            {
                RawRepresentationFactory = _ => new GenerateContentRequest { Tools = [new Tool { UrlContext = new UrlContextTool() }] },
                Tools = [aiFunction, new HostedWebSearchTool()]
            });

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var sentTools = sentDoc.RootElement.GetProperty("tools");
        Assert.Equal(3, sentTools.GetArrayLength());
        Assert.True(sentTools[0].TryGetProperty("urlContext", out _));
        Assert.Equal("get_weather", sentTools[1].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        Assert.True(sentTools[2].TryGetProperty("googleSearch", out _));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("///")]
    [InlineData(" / ")]
    public async Task GeminiEndpoint_SlashOnlyModelName_ThrowsArgumentException(string slashModel)
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);

        Assert.Throws<ArgumentException>(() => new GeminiClient(new GeminiClientOptions
        {
            Model = slashModel,
            ApiKey = "key"
        }, httpClient));

        using var client = CreateClient(httpClient);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateContentAsync("Hi", model: slashModel));
    }

    [Fact]
    public void ToolJsonConverter_ToResponseObject_StronglyTypedDictionaries_SerializeAsJsonObjectsWithoutReflection()
    {
        var emptyOptions = new JsonSerializerOptions();

        var stringDict = new Dictionary<string, string>
        {
            ["city"] = "Tokyo",
            ["condition"] = "Sunny"
        };
        var stringElement = ToolJsonConverter.ToResponseObject(stringDict, emptyOptions);
        Assert.Equal(JsonValueKind.Object, stringElement.ValueKind);
        Assert.Equal("Tokyo", stringElement.GetProperty("city").GetString());
        Assert.Equal("Sunny", stringElement.GetProperty("condition").GetString());

        var intDict = new Dictionary<string, int>
        {
            ["tempC"] = 24,
            ["humidity"] = 60
        };
        var intElement = ToolJsonConverter.ToResponseObject(intDict, emptyOptions);
        Assert.Equal(JsonValueKind.Object, intElement.ValueKind);
        Assert.Equal(24, intElement.GetProperty("tempC").GetInt32());
        Assert.Equal(60, intElement.GetProperty("humidity").GetInt32());

        var readOnlyDict = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>
            {
                ["nested"] = new List<Dictionary<string, int>>
                {
                    new() { ["score"] = 99 }
                }
            });
        var roElement = ToolJsonConverter.ToResponseObject(readOnlyDict, emptyOptions);
        Assert.Equal(JsonValueKind.Object, roElement.ValueKind);
        Assert.Equal(99, roElement.GetProperty("nested")[0].GetProperty("score").GetInt32());

        // A key/value sequence that is neither a dictionary nor described by type metadata asks for metadata.
        var customRoInt = new NonLegacyReadOnlyDictionary<int>(intDict);
        Assert.Throws<InvalidOperationException>(() => ToolJsonConverter.ToResponseObject(customRoInt, emptyOptions));
    }

    private sealed class NonLegacyReadOnlyDictionary<TValue>(IReadOnlyDictionary<string, TValue> inner) : IReadOnlyDictionary<string, TValue>
    {
        public TValue this[string key] => inner[key];
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<TValue> Values => inner.Values;
        public int Count => inner.Count;
        public bool ContainsKey(string key) => inner.ContainsKey(key);
        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value) => inner.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task StreamGenerateContentAsync_And_GetStreamingResponseAsync_RespectBothMethodAndEnumeratorCancellationTokens()
    {
        const string sse = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"chunk1\"}]}}]}\n\n";
        using var methodCts = new CancellationTokenSource();
        using var enumCts = new CancellationTokenSource();
        methodCts.Cancel();

        using var handler1 = new RecordingHandler(sse);
        using var httpClient1 = new HttpClient(handler1);
        using var client1 = CreateClient(httpClient1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client1
                .StreamGenerateContentAsync("Hi", cancellationToken: methodCts.Token)
                .WithCancellation(enumCts.Token))
            {
            }
        });

        using IChatClient chatClient = new GeminiChatClient(client1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in chatClient
                .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hi")], cancellationToken: methodCts.Token)
                .WithCancellation(enumCts.Token))
            {
            }
        });
    }

    [Fact]
    public void GeminiApiException_StandardConstructors_InitializeExpectedDefaults()
    {
        var exDefault = new GeminiApiException();
        Assert.Equal((HttpStatusCode)0, exDefault.StatusCode);
        Assert.Null(exDefault.ErrorCode);
        Assert.Null(exDefault.ErrorContent);

        var exMessage = new GeminiApiException("custom failure");
        Assert.Equal("custom failure", exMessage.Message);
        Assert.Equal((HttpStatusCode)0, exMessage.StatusCode);

        var inner = new InvalidOperationException("root cause");
        var exInner = new GeminiApiException("wrapped failure", inner);
        Assert.Equal("wrapped failure", exInner.Message);
        Assert.Same(inner, exInner.InnerException);
        Assert.Equal((HttpStatusCode)0, exInner.StatusCode);
    }

    private sealed class DelayedTrickleStream(byte[] data, int maxBytesPerRead, int delayPerReadMs) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length)
            {
                return 0;
            }

            await Task.Delay(delayPerReadMs, cancellationToken);
            var toCopy = Math.Min(Math.Min(buffer.Length, maxBytesPerRead), data.Length - _position);
            data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrickleStream(byte[] data, int maxBytesPerRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= data.Length)
            {
                return 0;
            }

            var toCopy = Math.Min(Math.Min(buffer.Length, maxBytesPerRead), data.Length - _position);
            data.AsSpan(_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubGeminiClient : IGeminiClient
    {
        public int GenerateCalls { get; private set; }
        public int EmbedCalls { get; private set; }

        public Uri Endpoint { get; } = GeminiClientOptions.DefaultEndpoint;
        public string? DefaultModel => ChatClientTests.DefaultModel;

        public Task<GenerateContentResponse> GenerateContentAsync(
            GenerateContentRequest request,
            string? model = null,
            CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            return Task.FromResult(new GenerateContentResponse
            {
                Candidates =
                [
                    new Candidate
                    {
                        Content = new Content { Role = Role.Model, Parts = [Part.FromText("from-custom-stub")] },
                        FinishReason = FinishReason.Stop
                    }
                ]
            });
        }

        public async IAsyncEnumerable<GenerateContentResponse> StreamGenerateContentAsync(
            GenerateContentRequest request,
            string? model = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new GenerateContentResponse();
        }

        public Task<EmbedContentResponse> EmbedContentAsync(
            EmbedContentRequest request,
            string? model = null,
            CancellationToken cancellationToken = default)
        {
            EmbedCalls++;
            return Task.FromResult(new EmbedContentResponse
            {
                Embedding = new ContentEmbedding { Values = [1.0f, 2.0f] }
            });
        }

        public Task<BatchEmbedContentsResponse> BatchEmbedContentsAsync(
            BatchEmbedContentsRequest request,
            string? model = null,
            CancellationToken cancellationToken = default)
        {
            EmbedCalls++;
            return Task.FromResult(new BatchEmbedContentsResponse
            {
                Embeddings = [new ContentEmbedding { Values = [1.0f, 2.0f] }]
            });
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void ChatRequestMapper_FunctionCallReplay_ReusesArgsWhenUnmodified_AndReserializesOnMutation()
    {
        using var rawArgsDoc = System.Text.Json.JsonDocument.Parse("""{"city":"Tokyo","days":3}""");
        var rawArgs = rawArgsDoc.RootElement.Clone();

        var modelResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts =
                        [
                            new Part
                            {
                                ThoughtSignature = "sig-1",
                                FunctionCall = new FunctionCall
                                {
                                    Name = "get_forecast",
                                    Args = rawArgs
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var mappedChat = ChatResponseMapper.ToChatResponse(modelResponse, "gemini-2.5-flash");
        var assistantMsg = mappedChat.Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());
        var resultMsg = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(fnCall.CallId, new Dictionary<string, object?> { ["temp"] = 22 })]);

        // 1. Unmodified arguments and still registers synthesized CallId for FunctionResultContent
        var reqUnmodified = ChatRequestMapper.ToRequest(
            [new ChatMessage(ChatRole.User, "Weather?"), assistantMsg, resultMsg],
            options: null,
            client: null!,
            modelId: "gemini-2.5-flash",
            System.Text.Json.JsonSerializerOptions.Default);

        var replayedCall = reqUnmodified.Contents[1].Parts[0].FunctionCall!;
        Assert.Null(replayedCall.Id);
        Assert.Equal("get_forecast", replayedCall.Name);
        Assert.Equal("sig-1", reqUnmodified.Contents[1].Parts[0].ThoughtSignature);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(rawArgs, replayedCall.Args!.Value));
        Assert.Equal("get_forecast", reqUnmodified.Contents[2].Parts[0].FunctionResponse!.Name);
        Assert.Null(reqUnmodified.Contents[2].Parts[0].FunctionResponse!.Id);

        // 2. Reordered keys with identical values still reuse the raw arguments via fallback check
        fnCall.Arguments!.Remove("city");
        fnCall.Arguments["city"] = rawArgs.GetProperty("city").Clone();
        var reqReordered = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: "gemini-2.5-flash",
            System.Text.Json.JsonSerializerOptions.Default);
        Assert.True(System.Text.Json.JsonElement.DeepEquals(rawArgs, reqReordered.Contents[0].Parts[0].FunctionCall!.Args!.Value));

        // 3. Mutated argument value: re-serializes updated value
        fnCall.Arguments!["city"] = "Osaka";
        var reqMutatedValue = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: "gemini-2.5-flash",
            System.Text.Json.JsonSerializerOptions.Default);
        Assert.Equal("Osaka", reqMutatedValue.Contents[0].Parts[0].FunctionCall!.Args!.Value.GetProperty("city").GetString());

        // 4. Added argument key: re-serializes with extra key
        fnCall.Arguments["city"] = rawArgs.GetProperty("city");
        fnCall.Arguments["unit"] = "C";
        var reqAddedKey = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: "gemini-2.5-flash",
            System.Text.Json.JsonSerializerOptions.Default);
        Assert.Equal("C", reqAddedKey.Contents[0].Parts[0].FunctionCall!.Args!.Value.GetProperty("unit").GetString());

        // 5. Removed argument key: re-serializes with remaining keys
        fnCall.Arguments.Remove("unit");
        fnCall.Arguments.Remove("days");
        var reqRemovedKey = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: "gemini-2.5-flash",
            System.Text.Json.JsonSerializerOptions.Default);
        Assert.False(reqRemovedKey.Contents[0].Parts[0].FunctionCall!.Args!.Value.TryGetProperty("days", out _));

        // 6. Duplicate keys in raw wire JSON + caller adding a key must NOT false-positive match the raw arguments
        using var dupDoc = System.Text.Json.JsonDocument.Parse("""{"city":"Tokyo","city":"Tokyo"}""");
        var dupResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [new Part { FunctionCall = new FunctionCall { Name = "dup_fn", Args = dupDoc.RootElement.Clone() } }]
                    }
                }
            ]
        };
        var dupChatMsg = ChatResponseMapper.ToChatResponse(dupResponse, "gemini-2.5-flash").Messages[0];
        var dupFnCall = Assert.Single(dupChatMsg.Contents.OfType<FunctionCallContent>());
        dupFnCall.Arguments!["unit"] = "celsius";
        var reqDupMutated = ChatRequestMapper.ToRequest(
            [dupChatMsg],
            options: null,
            client: null!,
            modelId: "gemini-2.5-flash",
            System.Text.Json.JsonSerializerOptions.Default);
        Assert.Equal("celsius", reqDupMutated.Contents[0].Parts[0].FunctionCall!.Args!.Value.GetProperty("unit").GetString());

        // 7. Undefined or Null the raw arguments with null Arguments normalizes FunctionCall.Args to null and passes Validate()
        using var nullDoc = System.Text.Json.JsonDocument.Parse("null");
        var undefinedAndNullResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts =
                        [
                            new Part { FunctionCall = new FunctionCall { Name = "undef_fn", Args = default } },
                            new Part { FunctionCall = new FunctionCall { Name = "null_fn", Args = nullDoc.RootElement.Clone() } }
                        ]
                    }
                }
            ]
        };
        var undefNullMsg = ChatResponseMapper.ToChatResponse(undefinedAndNullResponse, "gemini-2.5-flash").Messages[0];
        var reqUndefNull = ChatRequestMapper.ToRequest(
            [undefNullMsg],
            options: null,
            client: null!,
            modelId: "gemini-2.5-flash",
            System.Text.Json.JsonSerializerOptions.Default);
        reqUndefNull.Validate();
        Assert.Null(reqUndefNull.Contents[0].Parts[0].FunctionCall!.Args);
        Assert.Null(reqUndefNull.Contents[0].Parts[1].FunctionCall!.Args);
    }

    [Fact]
    public async Task BatchEmbedContentsAndEmbeddingGenerator_ModelNormalizationAndMaterializeInputs_EdgeCases()
    {
        using var handler = new StubBatchEmbedHandler();
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(
            new GeminiClientOptions
            {
                ApiKey = "test-key",
                Model = "gemini-embedding-001"
            },
            httpClient);

        // 1. Repeated bare model across items normalizes to resource path
        var repeatedBareRequest = new BatchEmbedContentsRequest
        {
            Requests =
            [
                new EmbedContentRequest { Model = "gemini-embedding-001", Content = new Content { Parts = [Part.FromText("a")] } },
                new EmbedContentRequest { Model = "gemini-embedding-001", Content = new Content { Parts = [Part.FromText("b")] } },
                new EmbedContentRequest { Model = "models/gemini-embedding-001", Content = new Content { Parts = [Part.FromText("c")] } }
            ]
        };
        var batchResult = await client.BatchEmbedContentsAsync(repeatedBareRequest);
        Assert.Equal(3, batchResult.Embeddings?.Count);

        // 2. Mismatched model at index 2 still throws ArgumentException after memoized index 0 and 1
        // 3. GeminiEmbeddingGenerator with non-IReadOnlyList<string> enumerable supporting TryGetNonEnumeratedCount (HashSet<string>)
        var generator = client.AsIEmbeddingGenerator();
        var hashSetInputs = new HashSet<string>(StringComparer.Ordinal) { "first", "second", "third" };
        var generated = await generator.GenerateAsync(hashSetInputs);
        Assert.Equal(3, generated.Count);
    }

    private sealed class StubBatchEmbedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string json = """
                {
                  "embeddings": [
                    { "values": [0.1, 0.2] },
                    { "values": [0.3, 0.4] },
                    { "values": [0.5, 0.6] }
                  ]
                }
                """;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ChunkedMemoryStream(params byte[][] chunks) : Stream
    {
        private int _chunkIndex;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_chunkIndex < chunks.Length)
            {
                var current = chunks[_chunkIndex];
                var remaining = current.Length - _offset;
                if (remaining <= 0)
                {
                    _chunkIndex++;
                    _offset = 0;
                    continue;
                }

                var toCopy = Math.Min(buffer.Length, remaining);
                current.AsMemory(_offset, toCopy).CopyTo(buffer);
                _offset += toCopy;
                if (_offset >= current.Length)
                {
                    _chunkIndex++;
                    _offset = 0;
                }

                return ValueTask.FromResult(toCopy);
            }

            return ValueTask.FromResult(0);
        }
    }
}
