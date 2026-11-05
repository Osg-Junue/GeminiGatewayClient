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
    public async Task Stream_SseFormat_MultipleDataLinesExceedingMaxEventBytesWithoutBlankLine_ThrowsGeminiApiException()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            sb.Append("data: {\"partial\":\"0123456789abcdef0123456789abcdef\"}\n");
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                stream,
                GeminiStreamingFormat.ServerSentEvents,
                HttpStatusCode.OK,
                watchdog,
                maxSseEventBytes: 128))
            {
            }
        });

        Assert.Equal(HttpStatusCode.OK, ex.StatusCode);
        Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(GeminiStreamingFormat.ServerSentEvents)]
    [InlineData(GeminiStreamingFormat.JsonArray)]
    public async Task Stream_EmptyHttp200Body_ThrowsGeminiApiExceptionInBothFormats(GeminiStreamingFormat format)
    {
        using var handler = new RecordingHandler(string.Empty, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key",
            StreamingFormat = format
        }, httpClient);

        await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in client.StreamGenerateContentAsync("Hi"))
            {
            }
        });
    }

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

    [Fact]
    public async Task ToModelContent_DoesNotCoalesceSubsequentTextOrThoughtIntoAlreadySignedPart()
    {
        const string sse = """
            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Thought 1A. ","thought":true}]}}]}

            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Thought 1B.","thought":true,"thoughtSignature":"sig-thought-1"}]}}]}

            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Thought 2A. ","thought":true}]}}]}

            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Thought 2B.","thought":true,"thoughtSignature":"sig-thought-2"}]}}]}

            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Answer 1.","thoughtSignature":"sig-ans-1"}]}}]}

            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Answer 2.","thoughtSignature":"sig-ans-2"}]},"finishReason":"STOP"}]}

            """;

        using var handler = new RecordingHandler(sse, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var modelContent = await client.StreamGenerateContentAsync("Hi").ToModelContentAsync();
        Assert.NotNull(modelContent);
        Assert.Equal(4, modelContent.Parts.Count);

        Assert.True(modelContent.Parts[0].Thought);
        Assert.Equal("Thought 1A. Thought 1B.", modelContent.Parts[0].Text);
        Assert.Equal("sig-thought-1", modelContent.Parts[0].ThoughtSignature);

        Assert.True(modelContent.Parts[1].Thought);
        Assert.Equal("Thought 2A. Thought 2B.", modelContent.Parts[1].Text);
        Assert.Equal("sig-thought-2", modelContent.Parts[1].ThoughtSignature);

        Assert.Null(modelContent.Parts[2].Thought);
        Assert.Equal("Answer 1.", modelContent.Parts[2].Text);
        Assert.Equal("sig-ans-1", modelContent.Parts[2].ThoughtSignature);

        Assert.Null(modelContent.Parts[3].Thought);
        Assert.Equal("Answer 2.", modelContent.Parts[3].Text);
        Assert.Equal("sig-ans-2", modelContent.Parts[3].ThoughtSignature);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IChatClient_MultiChunkStreamingTextWithTrailingThoughtSignature_ReattachesSignatureToCoalescedTextPartOnNextTurn(bool signatureInSeparateEmptyTextChunk)
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

        // Must be exactly 2 parts (1 thought + 1 coalesced text with signature re-attached), not 3 parts with an orphan signature-only part
        Assert.Equal(2, assistantParts.GetArrayLength());

        Assert.True(assistantParts[0].GetProperty("thought").GetBoolean());
        Assert.Equal("Thinking...", assistantParts[0].GetProperty("text").GetString());
        Assert.Equal("sig-thought-1", assistantParts[0].GetProperty("thoughtSignature").GetString());

        Assert.False(assistantParts[1].TryGetProperty("thought", out _));
        Assert.Equal("Hello world!", assistantParts[1].GetProperty("text").GetString());
        Assert.Equal("sig-text-final", assistantParts[1].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public void IChatClient_CodeExecutionContent_DetectsAdditionalPropertiesMutation_ClonesRawPart_AndSplitsCombinedOneof()
    {
        using var geminiClient = new GeminiClient("key", DefaultModel);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var rawExecPart = new Part
        {
            ExecutableCode = new ExecutableCode
            {
                Language = CodeExecutionLanguage.Python,
                Code = "print('original')"
            },
            ThoughtSignature = "sig-exec",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["customField"] = JsonSerializer.SerializeToElement("preserved")
            }
        };

        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [rawExecPart]
                    },
                    FinishReason = FinishReason.Stop
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        var codeItem = Assert.Single(chatResponse.Messages[0].Contents);
        Assert.Same(rawExecPart, codeItem.RawRepresentation);

        // 1. Unmodified item clones rawPart instead of sharing the mutable reference
        var unmodifiedRequest = ChatRequestMapper.ToRequest(
            [new ChatMessage(ChatRole.User, "Run"), chatResponse.Messages[0]],
            options: null,
            chatClient,
            DefaultModel,
            AIJsonUtilities.DefaultOptions);
        var replayedPart = Assert.Single(unmodifiedRequest.Contents[1].Parts);
        Assert.NotSame(rawExecPart, replayedPart);
        Assert.Equal("print('original')", replayedPart.ExecutableCode?.Code);
        Assert.True(replayedPart.ExtensionData?.ContainsKey("customField"));

        // 2. Mutating AdditionalProperties[ExecutableCode] rebuilds the Part with the updated code
        codeItem.AdditionalProperties![GeminiChatPropertyNames.ExecutableCode] = "print('mutated')";
        var mutatedRequest = ChatRequestMapper.ToRequest(
            [new ChatMessage(ChatRole.User, "Run"), chatResponse.Messages[0]],
            options: null,
            chatClient,
            DefaultModel,
            AIJsonUtilities.DefaultOptions);
        var rebuiltPart = Assert.Single(mutatedRequest.Contents[1].Parts);
        Assert.Equal("print('mutated')", rebuiltPart.ExecutableCode?.Code);
        Assert.Equal(CodeExecutionLanguage.Python, rebuiltPart.ExecutableCode?.Language);
        Assert.Equal("sig-exec", rebuiltPart.ThoughtSignature);

        // 3. AIContent with both ExecutableCode and CodeExecutionResult in AdditionalProperties splits into 2 valid Parts
        var combinedContent = new AIContent
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [GeminiChatPropertyNames.ExecutableCodeLanguage] = "Python",
                [GeminiChatPropertyNames.ExecutableCode] = "print(42)",
                [GeminiChatPropertyNames.CodeExecutionOutcome] = "Ok",
                [GeminiChatPropertyNames.CodeExecutionOutput] = "42\n",
                [GeminiChatPropertyNames.ThoughtSignature] = "sig-combined"
            }
        };

        var splitRequest = ChatRequestMapper.ToRequest(
            [new ChatMessage(ChatRole.Assistant, [combinedContent])],
            options: null,
            chatClient,
            DefaultModel,
            AIJsonUtilities.DefaultOptions);
        splitRequest.Validate();
        Assert.Equal(2, splitRequest.Contents[0].Parts.Count);
        Assert.Equal("print(42)", splitRequest.Contents[0].Parts[0].ExecutableCode?.Code);
        Assert.Null(splitRequest.Contents[0].Parts[0].CodeExecutionResult);
        Assert.Equal("42\n", splitRequest.Contents[0].Parts[1].CodeExecutionResult?.Output);
        Assert.Null(splitRequest.Contents[0].Parts[1].ExecutableCode);
        Assert.Equal("sig-combined", splitRequest.Contents[0].Parts[1].ThoughtSignature);
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

    [Fact]
    public async Task EmbedContent_AndBatchEmbedContents_ResolveRequestModelForEndpointUri_AndRejectMismatchedBatchModels()
    {
        const string embedJson = """{ "embedding": { "values": [0.5, 0.25] } }""";
        using var handler = new RecordingHandler(embedJson);
        using var httpClient = new HttpClient(handler);

        // 1. Client with only options.Model = "gemini-2.5-flash" (EmbeddingModel = null):
        //    request.Model = "gemini-embedding-001" must route URL to models/gemini-embedding-001:embedContent, not gemini-2.5-flash
        using var clientWithChatDefault = new GeminiClient(new GeminiClientOptions
        {
            Model = "gemini-2.5-flash",
            ApiKey = "test-key"
        }, httpClient);

        var singleReq = EmbedContentRequest.FromText("Embed with request.Model");
        singleReq.Model = "gemini-embedding-001";
        await clientWithChatDefault.EmbedContentAsync(singleReq);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent",
            handler.LastRequest?.RequestUri?.ToString());
        using (var doc = JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal("models/gemini-embedding-001", doc.RootElement.GetProperty("model").GetString());
        }

        // 2. Explicit model argument overrides request.Model on both URL and body without mutating caller's request
        await clientWithChatDefault.EmbedContentAsync(singleReq, model: "text-embedding-004");
        Assert.Equal("gemini-embedding-001", singleReq.Model);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent",
            handler.LastRequest?.RequestUri?.ToString());
        using (var doc = JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal("models/text-embedding-004", doc.RootElement.GetProperty("model").GetString());
        }

        // 3. Client with neither Model nor EmbeddingModel set succeeds when request.Model or item.Model is set
        const string batchJson = """{ "embeddings": [ { "values": [0.1] }, { "values": [0.2] } ] }""";
        using var batchHandler = new RecordingHandler(batchJson);
        using var batchHttp = new HttpClient(batchHandler);
        using var modelLessClient = new GeminiClient(new GeminiClientOptions
        {
            ApiKey = "test-key"
        }, batchHttp);

        var item0 = EmbedContentRequest.FromText("Doc 1");
        item0.Model = "gemini-embedding-001";
        var item1 = EmbedContentRequest.FromText("Doc 2");
        item1.Model = "models/gemini-embedding-001";

        await modelLessClient.BatchEmbedContentsAsync(new BatchEmbedContentsRequest
        {
            Requests = [item0, item1]
        });

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents",
            batchHandler.LastRequest?.RequestUri?.ToString());

        // 4. Conflicting item models within a batch or against the batch model argument throw ArgumentException
        var conflictingItem = EmbedContentRequest.FromText("Doc 2");
        conflictingItem.Model = "text-embedding-004";
        await Assert.ThrowsAsync<ArgumentException>(() => modelLessClient.BatchEmbedContentsAsync(new BatchEmbedContentsRequest
        {
            Requests = [item0, conflictingItem]
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => modelLessClient.BatchEmbedContentsAsync(
            new BatchEmbedContentsRequest { Requests = [item0] },
            model: "text-embedding-004"));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IChatClient_StreamingGroundingAndCitations_ResolvesCumulativeUtf8ByteOffsetsAcrossChunks(bool groundingInSeparateEmptyChunk)
    {
        // Cumulative text across chunks: "Hello 東京の天気は晴れです"
        //   Chunk 1: "Hello 東京の" (15 UTF-8 bytes, 9 UTF-16 chars)
        //     - "Hello " (6 ASCII bytes, chars 0..6)
        //     - "東京"   (6 UTF-8 bytes, cumulative bytes 6..12 -> segment-local bytes 6..12, chars 6..8)
        //     - "の"     (3 UTF-8 bytes, cumulative bytes 12..15 -> segment-local bytes 12..15, chars 8..9)
        //   Chunk 2: "天気は晴れです" (21 UTF-8 bytes, cumulative bytes 15..36, 7 UTF-16 chars)
        //     - "天気"   (6 UTF-8 bytes, cumulative bytes 15..21 -> segment-local bytes 0..6, chars 0..2)
        //     - "は"     (3 UTF-8 bytes, cumulative bytes 21..24 -> segment-local bytes 6..9, chars 2..3)
        //     - "晴れ"   (6 UTF-8 bytes, cumulative bytes 24..30 -> segment-local bytes 9..15, chars 3..5)
        //     - "です"   (6 UTF-8 bytes, cumulative bytes 30..36 -> segment-local bytes 15..21, chars 5..7)
        var sse = groundingInSeparateEmptyChunk
            ? """
              data: {"responseId":"r-g1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Hello 東京の"}]}}]}

              data: {"responseId":"r-g1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"天気は晴れです"}]}}]}

              data: {"responseId":"r-g1","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":""}]},"finishReason":"STOP","citationMetadata":{"citations":[{"startIndex":24,"endIndex":30,"uri":"https://jma.go.jp/forecast","title":"JMA"}]},"groundingMetadata":{"webSearchQueries":["東京 天気"],"groundingChunks":[{"web":{"uri":"https://weather.example.jp/tokyo","title":"Tokyo Weather"}}],"groundingSupports":[{"segment":{"partIndex":0,"startIndex":6,"endIndex":12,"text":"東京"},"groundingChunkIndices":[0]},{"segment":{"partIndex":0,"startIndex":15,"endIndex":21,"text":"天気"},"groundingChunkIndices":[0]}]}}]}

              """
            : """
              data: {"responseId":"r-g2","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Hello 東京の"}]}}]}

              data: {"responseId":"r-g2","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"天気は晴れです"}]},"finishReason":"STOP","citationMetadata":{"citations":[{"startIndex":24,"endIndex":30,"uri":"https://jma.go.jp/forecast","title":"JMA"}]},"groundingMetadata":{"webSearchQueries":["東京 天気"],"groundingChunks":[{"web":{"uri":"https://weather.example.jp/tokyo","title":"Tokyo Weather"}}],"groundingSupports":[{"segment":{"partIndex":0,"startIndex":6,"endIndex":12,"text":"東京"},"groundingChunkIndices":[0]},{"segment":{"partIndex":0,"startIndex":15,"endIndex":21,"text":"天気"},"groundingChunkIndices":[0]}]}}]}

              """;

        using var handler = new RecordingHandler(sse, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "東京の天気は？")]))
        {
            updates.Add(update);
        }

        // Ensure no empty TextContent("") was emitted in any update
        Assert.All(updates.SelectMany(u => u.Contents).OfType<TextContent>(), tc => Assert.NotEmpty(tc.Text));

        var response = updates.ToChatResponse();
        Assert.Equal("Hello 東京の天気は晴れです", response.Text);

        var textContents = response.Messages[0].Contents.OfType<TextContent>().ToList();
        Assert.Equal(2, textContents.Count);
        Assert.All(textContents, tc => Assert.NotEmpty(tc.Text));

        var chunk1Text = textContents[0];
        Assert.Equal("Hello 東京の", chunk1Text.Text);
        var chunk1Grounding = Assert.IsType<CitationAnnotation>(Assert.Single(chunk1Text.Annotations!));
        Assert.Equal("Tokyo Weather", chunk1Grounding.Title);
        var chunk1Region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(chunk1Grounding.AnnotatedRegions!));
        Assert.Equal(6, chunk1Region.StartIndex);
        Assert.Equal(8, chunk1Region.EndIndex);
        Assert.Equal("東京", chunk1Text.Text[chunk1Region.StartIndex!.Value..chunk1Region.EndIndex!.Value]);

        var chunk2Text = textContents[1];
        Assert.Equal("天気は晴れです", chunk2Text.Text);
        Assert.NotNull(chunk2Text.Annotations);
        Assert.Equal(2, chunk2Text.Annotations.Count);

        var citation = Assert.IsType<CitationAnnotation>(chunk2Text.Annotations[0]);
        Assert.Equal("JMA", citation.Title);
        var citationRegion = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(citation.AnnotatedRegions!));
        Assert.Equal(3, citationRegion.StartIndex);
        Assert.Equal(5, citationRegion.EndIndex);
        Assert.Equal("晴れ", chunk2Text.Text[citationRegion.StartIndex!.Value..citationRegion.EndIndex!.Value]);

        var chunk2Grounding = Assert.IsType<CitationAnnotation>(chunk2Text.Annotations[1]);
        Assert.Equal("Tokyo Weather", chunk2Grounding.Title);
        var chunk2Region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(chunk2Grounding.AnnotatedRegions!));
        Assert.Equal(0, chunk2Region.StartIndex);
        Assert.Equal(2, chunk2Region.EndIndex);
        Assert.Equal("天気", chunk2Text.Text[chunk2Region.StartIndex!.Value..chunk2Region.EndIndex!.Value]);
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
    public async Task HybridPartWithEmptyTextAndFunctionCallOrInlineData_NormalizesCleanlyOnReplay()
    {
        const string hybridJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "text": "",
                        "functionCall": { "id": "c1", "name": "get_weather", "args": { "city": "Tokyo" } },
                        "thoughtSignature": "sig-hybrid"
                      },
                      {
                        "text": "",
                        "inlineData": { "mimeType": "image/png", "data": "AQID" }
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        var chunk = JsonSerializer.Deserialize(hybridJson, GeminiGatewayClient.Serialization.GeminiJsonContext.Default.GenerateContentResponse)!;
        var modelContent = new[] { chunk }.ToModelContent();
        Assert.NotNull(modelContent);
        Assert.Equal(2, modelContent.Parts.Count);
        Assert.Null(modelContent.Parts[0].Text);
        Assert.Null(modelContent.Parts[1].Text);

        var replayRequest = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi"), modelContent]
        };
        replayRequest.Validate();

        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var chatResponse = ChatResponseMapper.ToChatResponse(chunk, DefaultModel);
        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Hi"),
            chatResponse.Messages[0],
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new Dictionary<string, object?> { ["ok"] = true })])
        ]);

        Assert.NotNull(handler.LastRequestBody);
    }

    [Fact]
    public async Task IChatClient_StreamingGroundingAndCitations_SpanCrossingMultipleChunks_AttachesToAllOverlappingSegmentsAndExposesCarrierInEmptyFinalUpdate()
    {
        // Chunk 1: "Hello 東京の" (15 UTF-8 bytes: "Hello " = 6B, "東京" = 6B [6..12), "の" = 3B [12..15))
        // Chunk 2: "天気は晴れ"   (15 UTF-8 bytes: "天気" = 6B [15..21), "は" = 3B [21..24), "晴れ" = 6B [24..30))
        // Citation spans bytes [6..21) ("東京の天気") across Chunk 1 and Chunk 2.
        // Grounding support omits startIndex (null -> 0) and ends at 21 ("Hello 東京の天気") across Chunk 1 and Chunk 2.
        const string sse = """
            data: {"responseId":"r-cross","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Hello 東京の"}]}}]}

            data: {"responseId":"r-cross","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"天気は晴れ"}]}}]}

            data: {"responseId":"r-cross","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":""}]},"finishReason":"STOP","citationMetadata":{"citations":[{"startIndex":6,"endIndex":21,"uri":"https://example.jp/cite","title":"CrossChunkCite"}]},"groundingMetadata":{"groundingChunks":[{"web":{"uri":"https://example.jp/ground","title":"CrossChunkGround"}}],"groundingSupports":[{"segment":{"partIndex":0,"endIndex":21,"text":"Hello 東京の天気"},"groundingChunkIndices":[0]}]}}]}

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

        Assert.Equal(3, updates.Count);

        // Real-time consumer of the final empty-text metadata update must see the annotations on a non-TextContent carrier
        var finalCarrier = Assert.Single(updates[2].Contents);
        Assert.IsNotType<TextContent>(finalCarrier);
        Assert.NotNull(finalCarrier.Annotations);
        Assert.Equal(2, finalCarrier.Annotations.Count);
        var carrierCite = Assert.IsType<CitationAnnotation>(finalCarrier.Annotations[0]);
        Assert.NotNull(carrierCite.AnnotatedRegions);
        Assert.Equal(2, carrierCite.AnnotatedRegions.Count);
        var carrierGround = Assert.IsType<CitationAnnotation>(finalCarrier.Annotations[1]);
        Assert.NotNull(carrierGround.AnnotatedRegions);
        Assert.Equal(2, carrierGround.AnnotatedRegions.Count);

        // Standalone ToChatResponseUpdate without prior streamedSegments must also emit a carrier rather than TextContent("")
        var standaloneChunk = Assert.IsType<GenerateContentResponse>(updates[2].RawRepresentation);
        var standaloneUpdate = ChatResponseMapper.ToChatResponseUpdate(standaloneChunk, DefaultModel);
        Assert.Empty(standaloneUpdate.Contents.OfType<TextContent>());
        var standaloneCarrier = Assert.Single(standaloneUpdate.Contents);
        Assert.NotNull(standaloneCarrier.Annotations);
        Assert.Equal(2, standaloneCarrier.Annotations.Count);

        // Multi-part streaming chunk with interleaved non-text part and explicit non-zero partIndex must target the exact part
        const string multiPartChunkJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      { "functionCall": { "id": "fc1", "name": "noop", "args": {} } },
                      { "text": "Intro: " },
                      { "text": "😀晴れ" }
                    ]
                  },
                  "groundingMetadata": {
                    "groundingChunks": [
                      { "web": { "uri": "https://weather.example.jp/emoji", "title": "Emoji Forecast" } }
                    ],
                    "groundingSupports": [
                      {
                        "segment": { "partIndex": 2, "startIndex": 4, "endIndex": 10, "text": "晴れ" },
                        "groundingChunkIndices": [0]
                      }
                    ]
                  }
                }
              ]
            }
            """;
        var multiPartChunk = JsonSerializer.Deserialize(multiPartChunkJson, GeminiGatewayClient.Serialization.GeminiJsonContext.Default.GenerateContentResponse)!;
        List<ChatResponseMapper.StreamedTextSegment>? multiSegments = null;
        var multiBytes = 0;
        var multiUpdate = ChatResponseMapper.ToChatResponseUpdate(multiPartChunk, DefaultModel, ref multiSegments, ref multiBytes);
        var multiTexts = multiUpdate.Contents.OfType<TextContent>().ToList();
        Assert.Equal(2, multiTexts.Count);
        Assert.Null(multiTexts[0].Annotations);
        var multiPartCite = Assert.IsType<CitationAnnotation>(Assert.Single(multiTexts[1].Annotations!));
        var multiPartRegion = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(multiPartCite.AnnotatedRegions!));
        Assert.Equal("晴れ", multiTexts[1].Text[multiPartRegion.StartIndex!.Value..multiPartRegion.EndIndex!.Value]);

        // Aggregated response must coalesce text cleanly and preserve overlapping local regions on each segment
        var response = updates.ToChatResponse();
        Assert.Equal("Hello 東京の天気は晴れ", response.Text);

        var textContents = response.Messages[0].Contents.OfType<TextContent>().ToList();
        Assert.Equal(2, textContents.Count);

        var seg1 = textContents[0];
        Assert.Equal("Hello 東京の", seg1.Text);
        Assert.NotNull(seg1.Annotations);
        Assert.Equal(2, seg1.Annotations.Count);
        var seg1Cite = Assert.IsType<CitationAnnotation>(seg1.Annotations[0]);
        var seg1CiteRegion = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(seg1Cite.AnnotatedRegions!));
        Assert.Equal("東京の", seg1.Text[seg1CiteRegion.StartIndex!.Value..seg1CiteRegion.EndIndex!.Value]);
        var seg1Ground = Assert.IsType<CitationAnnotation>(seg1.Annotations[1]);
        var seg1GroundRegion = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(seg1Ground.AnnotatedRegions!));
        Assert.Equal("Hello 東京の", seg1.Text[seg1GroundRegion.StartIndex!.Value..seg1GroundRegion.EndIndex!.Value]);

        var seg2 = textContents[1];
        Assert.Equal("天気は晴れ", seg2.Text);
        Assert.NotNull(seg2.Annotations);
        Assert.Equal(2, seg2.Annotations.Count);
        var seg2Cite = Assert.IsType<CitationAnnotation>(seg2.Annotations[0]);
        var seg2CiteRegion = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(seg2Cite.AnnotatedRegions!));
        Assert.Equal("天気", seg2.Text[seg2CiteRegion.StartIndex!.Value..seg2CiteRegion.EndIndex!.Value]);
        var seg2Ground = Assert.IsType<CitationAnnotation>(seg2.Annotations[1]);
        var seg2GroundRegion = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(seg2Ground.AnnotatedRegions!));
        Assert.Equal("天気", seg2.Text[seg2GroundRegion.StartIndex!.Value..seg2GroundRegion.EndIndex!.Value]);
    }

    [Fact]
    public async Task StreamGenerateContentAsync_JsonArrayFormat_SplitErrorResponseAcrossReads_DrainsAndParsesStructuredError()
    {
        const string errorJson = """{"error":{"code":429,"message":"Quota exceeded split across reads","status":"RESOURCE_EXHAUSTED"}}""";
        var errorBytes = Encoding.UTF8.GetBytes(errorJson);

        using var handler = new DelegatingStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TrickleStream(errorBytes, maxBytesPerRead: 5))
        });
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key",
            StreamingFormat = GeminiStreamingFormat.JsonArray
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in client.StreamGenerateContentAsync("Hi"))
            {
            }
        });

        Assert.Equal(429, ex.ErrorCode);
        Assert.Equal("RESOURCE_EXHAUSTED", ex.ErrorStatus);
        Assert.Equal("Quota exceeded split across reads", ex.ErrorMessage);
    }

    [Fact]
    public async Task ReadJsonArrayAsync_ElementExceedingMaxBytes_ThrowsGeminiApiException()
    {
        var oversizedText = new string('A', 256);
        var json = "[{\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"" + oversizedText + "\"}]}}]}]";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                               stream,
                               GeminiStreamingFormat.JsonArray,
                               HttpStatusCode.OK,
                               watchdog,
                               maxSseEventBytes: 128))
            {
            }
        });

        Assert.Contains("exceeded the maximum allowed length", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IChatClient_MultiChunkStreaming_ToChatResponse_ThenPersistedWithoutRawRepresentation_ReattachesThoughtSignature()
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
            Assert.Equal(1, preParts.GetArrayLength());
            Assert.Equal("Hello world!", preParts[0].GetProperty("text").GetString());
            Assert.Equal("sig-persisted-final", preParts[0].GetProperty("thoughtSignature").GetString());
            Assert.Equal("keep-sig-extra", preParts[0].GetProperty("sigExtra").GetString());
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
        Assert.Equal(1, parts.GetArrayLength());
        Assert.Equal("Hello world!", parts[0].GetProperty("text").GetString());
        Assert.Equal("sig-persisted-final", parts[0].GetProperty("thoughtSignature").GetString());
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
    public async Task TryGetModelContent_NonStreamingHybridPartWithEmptyTextAndExtensionData_PreservesExtensionDataAndValidates()
    {
        const string hybridJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "text": "",
                        "functionCall": { "id": "c-ext", "name": "lookup", "args": { "q": "test" } },
                        "thoughtSignature": "sig-ext",
                        "vendorExtra": "keep-me"
                      },
                      {
                        "text": "",
                        "inlineData": { "mimeType": "image/png", "data": "AQID" },
                        "blobExtra": "keep-blob"
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize(hybridJson, GeminiGatewayClient.Serialization.GeminiJsonContext.Default.GenerateContentResponse)!;
        Assert.True(response.TryGetModelContent(out var modelContent));
        Assert.NotNull(modelContent);
        Assert.Equal(2, modelContent.Parts.Count);
        Assert.Null(modelContent.Parts[0].Text);
        Assert.NotNull(modelContent.Parts[0].ExtensionData);
        Assert.Equal("keep-me", modelContent.Parts[0].ExtensionData!["vendorExtra"].GetString());
        Assert.Null(modelContent.Parts[1].Text);
        Assert.NotNull(modelContent.Parts[1].ExtensionData);
        Assert.Equal("keep-blob", modelContent.Parts[1].ExtensionData!["blobExtra"].GetString());

        var directRequest = new GenerateContentRequest { Contents = [Content.FromText("Hi"), modelContent] };
        directRequest.Validate();

        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Hi"),
            chatResponse.Messages[0],
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c-ext", new Dictionary<string, object?> { ["ok"] = 1 })])
        ]);

        Assert.NotNull(handler.LastRequestBody);
        using var sentDoc = JsonDocument.Parse(handler.LastRequestBody);
        var replayedParts = sentDoc.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(2, replayedParts.GetArrayLength());
        Assert.False(replayedParts[0].TryGetProperty("text", out _));
        Assert.Equal("keep-me", replayedParts[0].GetProperty("vendorExtra").GetString());
        Assert.False(replayedParts[1].TryGetProperty("text", out _));
        Assert.Equal("keep-blob", replayedParts[1].GetProperty("blobExtra").GetString());
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
        const string singleEmbedJson = """{"embedding":{"values":[0.1,0.2]}}""";
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
        Assert.Equal(256, singleDoc.RootElement.GetProperty("outputDimensionality").GetInt32());

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
    public async Task IChatClient_RawRepresentationFactoryWithTools_MergesWithChatOptionsToolsWithoutMutatingTemplate()
    {
        var sharedTemplate = new GenerateContentRequest
        {
            Tools = [new Tool { UrlContext = new UrlContextTool() }]
        };

        var aiFunction = AIFunctionFactory.Create((string city) => city, "get_weather", "Gets weather");

        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var geminiClient = CreateClient(httpClient);
        IChatClient chatClient = geminiClient.AsIChatClient();

        await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Weather?")],
            new ChatOptions
            {
                RawRepresentationFactory = _ => sharedTemplate,
                Tools = [aiFunction, new HostedWebSearchTool()]
            });

        Assert.Single(sharedTemplate.Tools!);
        Assert.NotNull(sharedTemplate.Tools![0].UrlContext);
        Assert.Null(sharedTemplate.Tools![0].FunctionDeclarations);

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
    public void VideoOffsetJsonConverter_NonScalarSecondsOrNanos_SkipsCleanlyWithoutThrowing()
    {
        const string json = """{"startOffset":{"seconds":{"nested":1},"nanos":[1,2,3],"extra":true},"endOffset":{"seconds":"10","nanos":500}}""";
        var metadata = JsonSerializer.Deserialize(json, GeminiGatewayClient.Serialization.GeminiJsonContext.Default.VideoMetadata);
        Assert.NotNull(metadata);
        Assert.Equal(TimeSpan.Zero, metadata.StartOffset?.ToTimeSpan());
        Assert.Equal(TimeSpan.FromSeconds(10) + TimeSpan.FromTicks(5), metadata.EndOffset?.ToTimeSpan());
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

        // Custom IReadOnlyDictionary<string, T> that does NOT implement non-generic System.Collections.IDictionary,
        // plus LINQ IEnumerable<KeyValuePair<string, int>> projection.
        var customRoString = new NonLegacyReadOnlyDictionary<string>(stringDict);
        var customRoElement = ToolJsonConverter.ToResponseObject(customRoString, emptyOptions);
        Assert.Equal(JsonValueKind.Object, customRoElement.ValueKind);
        Assert.Equal("Tokyo", customRoElement.GetProperty("city").GetString());

        var customRoInt = new NonLegacyReadOnlyDictionary<int>(intDict);
        var customRoIntElement = ToolJsonConverter.ToResponseObject(customRoInt, emptyOptions);
        Assert.Equal(JsonValueKind.Object, customRoIntElement.ValueKind);
        Assert.Equal(24, customRoIntElement.GetProperty("tempC").GetInt32());

        var linqFiltered = intDict.Where(kv => kv.Value > 30);
        var linqElement = ToolJsonConverter.ToResponseObject(linqFiltered, emptyOptions);
        Assert.Equal(JsonValueKind.Object, linqElement.ValueKind);
        Assert.Equal(60, linqElement.GetProperty("humidity").GetInt32());
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
    public void ChatResponseMapper_MapUsage_WhenTotalsAlreadyIncludeSubcomponents_DoesNotDoubleCount()
    {
        // Case 1: Gateway/endpoint where PromptTokenCount (7) already includes ToolUsePromptTokenCount (2)
        // and CandidatesTokenCount (7) already includes ThoughtsTokenCount (3), so 7 + 7 == 14 (TotalTokenCount).
        var inclusiveResponse = new GenerateContentResponse
        {
            Candidates = [new Candidate { Content = new Content { Role = Role.Model, Parts = [Part.FromText("ok")] }, FinishReason = FinishReason.Stop }],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 7,
                ToolUsePromptTokenCount = 2,
                CandidatesTokenCount = 7,
                ThoughtsTokenCount = 3,
                TotalTokenCount = 14
            }
        };

        var mappedInclusive = ChatResponseMapper.ToChatResponse(inclusiveResponse, DefaultModel);
        Assert.NotNull(mappedInclusive.Usage);
        Assert.Equal(7, mappedInclusive.Usage!.InputTokenCount);
        Assert.Equal(7, mappedInclusive.Usage.OutputTokenCount);
        Assert.Equal(14, mappedInclusive.Usage.TotalTokenCount);
        Assert.Equal(3, mappedInclusive.Usage.ReasoningTokenCount);

        // Case 2: CandidatesTokenCount is exclusive of ThoughtsTokenCount (4 + 3 = 7),
        // while PromptTokenCount (7) already includes ToolUsePromptTokenCount (2), total = 14.
        var halfInclusiveResponse = new GenerateContentResponse
        {
            Candidates = [new Candidate { Content = new Content { Role = Role.Model, Parts = [Part.FromText("ok")] }, FinishReason = FinishReason.Stop }],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 7,
                ToolUsePromptTokenCount = 2,
                CandidatesTokenCount = 4,
                ThoughtsTokenCount = 3,
                TotalTokenCount = 14
            }
        };

        var mappedHalf = ChatResponseMapper.ToChatResponse(halfInclusiveResponse, DefaultModel);
        Assert.NotNull(mappedHalf.Usage);
        Assert.Equal(7, mappedHalf.Usage!.InputTokenCount);
        Assert.Equal(7, mappedHalf.Usage.OutputTokenCount);
        Assert.Equal(14, mappedHalf.Usage.TotalTokenCount);

        // Case 3: TotalTokenCount is omitted in JSON (0) while PromptTokenCount (5), CandidatesTokenCount (4), and ThoughtsTokenCount (3) are populated.
        var omittedTotalResponse = new GenerateContentResponse
        {
            Candidates = [new Candidate { Content = new Content { Role = Role.Model, Parts = [Part.FromText("ok")] }, FinishReason = FinishReason.Stop }],
            UsageMetadata = new UsageMetadata
            {
                PromptTokenCount = 5,
                CandidatesTokenCount = 4,
                ThoughtsTokenCount = 3,
                TotalTokenCount = 0
            }
        };

        var mappedOmittedTotal = ChatResponseMapper.ToChatResponse(omittedTotalResponse, DefaultModel);
        Assert.NotNull(mappedOmittedTotal.Usage);
        Assert.Equal(5, mappedOmittedTotal.Usage!.InputTokenCount);
        Assert.Equal(7, mappedOmittedTotal.Usage.OutputTokenCount);
        Assert.Equal(12, mappedOmittedTotal.Usage.TotalTokenCount);
        Assert.Equal(3, mappedOmittedTotal.Usage.ReasoningTokenCount);
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
    public async Task StreamIdleWatchdog_TimerFiresBetweenReads_RefreshesSpentTokenOnNextReadInsteadOfFalseTimeout()
    {
        using var callerCts = new CancellationTokenSource();
        using var watchdog = new StreamIdleWatchdog(TimeSpan.FromMilliseconds(15), callerCts.Token);

        // Simulate a read where the idle timer expires right as the read completes,
        // leaving the internal CTS canceled while the caller token is still active.
        watchdog.BeforeRead();
        var firstToken = watchdog.Token;
        await Task.Delay(40);
        Assert.True(firstToken.IsCancellationRequested);
        watchdog.AfterRead(succeeded: true);

        // The next read must obtain a fresh non-canceled token rather than failing immediately.
        watchdog.BeforeRead();
        var secondToken = watchdog.Token;
        Assert.False(secondToken.IsCancellationRequested);
        watchdog.AfterRead(succeeded: true);
    }

    [Fact]
    public async Task ToModelContent_And_ToModelContentAsync_LargeChunkSequence_AggregatesTextAndThoughtsAccurately()
    {
        var chunks = new List<GenerateContentResponse>(capacity: 205);
        var expectedThought = new StringBuilder();
        var expectedText = new StringBuilder();

        for (var i = 0; i < 100; i++)
        {
            var piece = $"thought-{i};";
            expectedThought.Append(piece);
            chunks.Add(new GenerateContentResponse
            {
                Candidates =
                [
                    new Candidate
                    {
                        Content = new Content
                        {
                            Role = Role.Model,
                            Parts = [new Part { Text = piece, Thought = true }]
                        }
                    }
                ]
            });
        }

        // Empty text carrier with ThoughtSignature that should merge into the preceding thought part
        const string sig = "AQIDBA==";
        chunks.Add(new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [new Part { Text = string.Empty, Thought = true, ThoughtSignature = sig }]
                    }
                }
            ]
        });

        for (var i = 0; i < 100; i++)
        {
            var piece = $"text-{i};";
            expectedText.Append(piece);
            chunks.Add(new GenerateContentResponse
            {
                Candidates =
                [
                    new Candidate
                    {
                        Content = new Content
                        {
                            Role = Role.Model,
                            Parts = [Part.FromText(piece)]
                        }
                    }
                ]
            });
        }

        var syncContent = chunks.ToModelContent();
        Assert.NotNull(syncContent);
        Assert.Equal(Role.Model, syncContent!.Role);
        Assert.NotNull(syncContent.Parts);
        Assert.Equal(2, syncContent.Parts!.Count);
        Assert.True(syncContent.Parts[0].Thought);
        Assert.Equal(expectedThought.ToString(), syncContent.Parts[0].Text);
        Assert.Equal(sig, syncContent.Parts[0].ThoughtSignature);
        Assert.Null(syncContent.Parts[1].Thought);
        Assert.Equal(expectedText.ToString(), syncContent.Parts[1].Text);

        var asyncContent = await ToAsyncEnumerable(chunks).ToModelContentAsync();
        Assert.NotNull(asyncContent);
        Assert.NotNull(asyncContent!.Parts);
        Assert.Equal(2, asyncContent.Parts!.Count);
        Assert.Equal(expectedThought.ToString(), asyncContent.Parts[0].Text);
        Assert.Equal(sig, asyncContent.Parts[0].ThoughtSignature);
        Assert.Equal(expectedText.ToString(), asyncContent.Parts[1].Text);

        static async IAsyncEnumerable<GenerateContentResponse> ToAsyncEnumerable(IEnumerable<GenerateContentResponse> items)
        {
            foreach (var item in items)
            {
                await Task.Yield();
                yield return item;
            }
        }
    }

    [Fact]
    public void PooledByteBufferWriter_AccessAfterDispose_ThrowsObjectDisposedExceptionAndNeverReRentsBuffer()
    {
        var writer = new PooledByteBufferWriter(initialCapacity: 64);
        var span = writer.GetSpan(4);
        "test"u8.CopyTo(span);
        writer.Advance(4);
        Assert.Equal(4, writer.WrittenCount);

        writer.Dispose();
        writer.Dispose(); // Idempotent second dispose

        Assert.Throws<ObjectDisposedException>(() => _ = writer.WrittenCount);
        Assert.Throws<ObjectDisposedException>(() => _ = writer.WrittenSpan);
        Assert.Throws<ObjectDisposedException>(() => _ = writer.WrittenMemory);
        Assert.Throws<ObjectDisposedException>(() => writer.Clear());
        Assert.Throws<ObjectDisposedException>(() => writer.Advance(1));
        Assert.Throws<ObjectDisposedException>(() => _ = writer.GetMemory(16));
        Assert.Throws<ObjectDisposedException>(() => _ = writer.GetSpan(16));
    }

    [Fact]
    public void ChatOptionsMapper_ApplyAdditionalProperties_MapsKnownAndExtensionPropertiesRespectingPrecedence()
    {
        var stubClient = new StubGeminiClient();
        using var chatClient = new GeminiChatClient(stubClient);
        var aotOptions = new JsonSerializerOptions();
        using var jsonDoc = JsonDocument.Parse("""{"logprobs":5,"responseLogprobs":true,"mediaResolution":"MEDIA_RESOLUTION_HIGH","responseModalities":["TEXT","AUDIO"]}""");

        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["candidateCount"] = 2L, // boxed long
                ["temperature"] = 0.25f, // boxed float
                ["responseLogprobs"] = jsonDoc.RootElement.GetProperty("responseLogprobs"),
                ["logprobs"] = jsonDoc.RootElement.GetProperty("logprobs"),
                ["mediaResolution"] = jsonDoc.RootElement.GetProperty("mediaResolution"),
                ["responseModalities"] = jsonDoc.RootElement.GetProperty("responseModalities"),
                ["cachedContent"] = "cachedContents/abc-123",
                ["store"] = true,
                ["labels"] = new Dictionary<string, string> { ["env"] = "prod" },
                ["speechConfig"] = new Dictionary<string, string> { ["voiceName"] = "Kore" },
                ["customGatewayFlag"] = new Dictionary<string, string> { ["region"] = "ap-northeast1" }
            }
        };

        var request = ChatRequestMapper.ToRequest(
            [new ChatMessage(ChatRole.User, "Hi")],
            options,
            chatClient,
            DefaultModel,
            aotOptions);

        Assert.Equal("cachedContents/abc-123", request.CachedContent);
        Assert.True(request.Store);
        Assert.Equal("prod", request.Labels!["env"]);
        Assert.NotNull(request.GenerationConfig);
        Assert.Equal(2, request.GenerationConfig!.CandidateCount);
        Assert.Equal(0.25, request.GenerationConfig.Temperature!.Value, precision: 4);
        Assert.True(request.GenerationConfig.ResponseLogprobs);
        Assert.Equal(5, request.GenerationConfig.Logprobs);
        Assert.Equal(MediaResolution.High, request.GenerationConfig.MediaResolution);
        Assert.Equal(["TEXT", "AUDIO"], request.GenerationConfig.ResponseModalities);
        Assert.Equal("Kore", request.GenerationConfig.ExtensionData!["speechConfig"].GetProperty("voiceName").GetString());
        Assert.NotNull(request.ExtensionData);
        Assert.Single(request.ExtensionData!);
        Assert.Equal("ap-northeast1", request.ExtensionData!["customGatewayFlag"].GetProperty("region").GetString());

        // Verify RawRepresentationFactory precedence: existing values on the template are not overwritten
        // AND must not leak into request.ExtensionData (which would emit duplicate JSON keys on the wire).
        var optionsWithTemplate = new ChatOptions
        {
            RawRepresentationFactory = _ => new GenerateContentRequest
            {
                CachedContent = "cachedContents/from-template",
                Store = false,
                GenerationConfig = new GenerationConfig { CandidateCount = 1 }
            },
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["candidateCount"] = 4L,
                ["cachedContent"] = "cachedContents/from-additional-props",
                ["store"] = true
            }
        };

        var templatedRequest = ChatRequestMapper.ToRequest(
            [new ChatMessage(ChatRole.User, "Hi")],
            optionsWithTemplate,
            chatClient,
            DefaultModel,
            aotOptions);
        Assert.Equal("cachedContents/from-template", templatedRequest.CachedContent);
        Assert.False(templatedRequest.Store);
        Assert.Equal(1, templatedRequest.GenerationConfig!.CandidateCount);
        Assert.Null(templatedRequest.ExtensionData);

        var wireJson = JsonSerializer.Serialize(templatedRequest, GeminiGatewayClient.Serialization.GeminiJsonContext.Default.GenerateContentRequest);
        using var wireDoc = JsonDocument.Parse(wireJson);
        Assert.Equal("cachedContents/from-template", wireDoc.RootElement.GetProperty("cachedContent").GetString());
        Assert.False(wireDoc.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(1, wireDoc.RootElement.GetProperty("generationConfig").GetProperty("candidateCount").GetInt32());
    }

    [Fact]
    public void GeminiErrorParser_InStreamErrorWith2xxCode_MapsToNonSuccessStatusCode()
    {
        using var doc1 = JsonDocument.Parse("""{"error":{"code":200,"message":"Backend stream error","status":"UNAVAILABLE"}}""");
        var ex1 = GeminiErrorParser.TryCreateFromStreamPayload(doc1.RootElement, HttpStatusCode.OK);
        Assert.NotNull(ex1);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex1!.StatusCode);
        Assert.Equal(200, ex1.ErrorCode);

        using var doc2 = JsonDocument.Parse("""{"error":{"code":200,"message":"Unknown stream error"}}""");
        var ex2 = GeminiErrorParser.TryCreateFromStreamPayload(doc2.RootElement, HttpStatusCode.OK);
        Assert.NotNull(ex2);
        Assert.Equal(HttpStatusCode.InternalServerError, ex2!.StatusCode);
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

    [Fact]
    public async Task GeminiErrorParser_CreateFromResponseAsync_UsesPerReadWatchdogRatherThanCumulativeTimeout()
    {
        // 4 chunks x 25ms = 100ms total read duration, while idle timeout per read is 65ms.
        // A cumulative CTS would time out at 65ms, whereas per-read watchdog arming succeeds.
        const string errorJson = """{"error":{"code":503,"message":"Slow trickle error body","status":"UNAVAILABLE"}}""";
        var errorBytes = Encoding.UTF8.GetBytes(errorJson);

        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StreamContent(new DelayedTrickleStream(errorBytes, maxBytesPerRead: 24, delayPerReadMs: 25))
        };
        using var watchdog = new StreamIdleWatchdog(TimeSpan.FromMilliseconds(65), CancellationToken.None);

        var ex = await GeminiErrorParser.CreateFromResponseAsync(response, watchdog);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal("Slow trickle error body", ex.ErrorMessage);
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
    public async Task GeminiStreamReader_SingleLineFastPath_PreservesBomBlankLineSplitCrlfAndMultilineBehavior()
    {
        // Chunk 1: UTF-8 BOM + single-line event (\n\n), empty data lines, multi-line data event,
        // single-line data followed by comment (:keepalive) and id/event lines before blank line,
        // and a single-line event whose blank-line \r\n is split across two stream reads right after \r.
        var chunk1 =
            "\uFEFFdata: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"bom-first\"}]}}]}\n\n" +
            "data:\r\n\r\n" +
            "data: \n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\n" +
            "data: \"parts\":[{\"text\":\"multiline\"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"with-comment-and-id\"}]}}]}\n" +
            ":keepalive\n" +
            "id: 42\r\n" +
            "event: message\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"split-crlf\"}]}}]}\r\n\r";
        var chunk2 =
            "\ndata: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"after-split\"}]}}]}\r\n\r\n" +
            "data: [DONE]\r\n\r\n";

        await using var stream = new ChunkedMemoryStream(
            System.Text.Encoding.UTF8.GetBytes(chunk1),
            System.Text.Encoding.UTF8.GetBytes(chunk2));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var results = new List<string?>();
        await foreach (var response in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            System.Net.HttpStatusCode.OK,
            watchdog))
        {
            results.Add(response.Text);
        }

        Assert.Equal(["bom-first", "multiline", "with-comment-and-id", "split-crlf", "after-split"], results);
    }

    [Fact]
    public void ChatResponseMapper_GroundingCitations_DeduplicatesPrimaryAndMultiSegmentStreamingTargets()
    {
        const string text = "Alpha Beta Gamma Delta";
        var chunk0 = new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/0", Title = "Zero" } };
        var chunk1 = new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/1", Title = "One" } };
        var chunkUnreferenced = new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/2", Title = "Two" } };

        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText(text)] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks = [chunk0, chunk1, chunkUnreferenced],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { StartIndex = 0, EndIndex = 5, Text = "Alpha" },
                                GroundingChunkIndices = [0, 1]
                            },
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { StartIndex = 6, EndIndex = 10, Text = "Beta" },
                                GroundingChunkIndices = [0]
                            },
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { StartIndex = 11, EndIndex = 16, Text = "Gamma" },
                                GroundingChunkIndices = []
                            }
                        ]
                    }
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, "gemini-2.5-flash");
        var textContent = Assert.Single(chatResponse.Messages[0].Contents.OfType<TextContent>());
        Assert.NotNull(textContent.Annotations);

        // 2 referenced chunks (deduplicated across supports) + 1 unattached support + 1 unreferenced chunk = 4 annotations
        var citations = textContent.Annotations.OfType<CitationAnnotation>().ToList();
        Assert.Equal(4, citations.Count);
        Assert.Equal("Zero", citations[0].Title);
        Assert.Equal("Alpha", citations[0].Snippet);
        Assert.Equal(2, citations[0].AnnotatedRegions?.Count);
        Assert.Equal("One", citations[1].Title);
        Assert.Single(citations[1].AnnotatedRegions!);
        Assert.Equal("Gamma", citations[2].Snippet);
        Assert.Equal("Two", citations[3].Title);

        // Also verify multi-segment streaming resolution (spanning 1 segment vs >=2 segments) + realtime carrier
        List<ChatResponseMapper.StreamedTextSegment>? streamedSegments = null;
        var cumulativeBytes = 0;
        _ = ChatResponseMapper.ToChatResponseUpdate(
            new GenerateContentResponse
            {
                Candidates = [new Candidate { Content = new Content { Role = Role.Model, Parts = [Part.FromText("Alpha ")] } }]
            },
            "gemini-2.5-flash",
            ref streamedSegments,
            ref cumulativeBytes);
        _ = ChatResponseMapper.ToChatResponseUpdate(
            new GenerateContentResponse
            {
                Candidates = [new Candidate { Content = new Content { Role = Role.Model, Parts = [Part.FromText("Beta Gamma Delta")] } }]
            },
            "gemini-2.5-flash",
            ref streamedSegments,
            ref cumulativeBytes);
        var finalUpdate = ChatResponseMapper.ToChatResponseUpdate(
            new GenerateContentResponse
            {
                Candidates =
                [
                    new Candidate
                    {
                        Content = new Content { Role = Role.Model, Parts = [] },
                        FinishReason = FinishReason.Stop,
                        GroundingMetadata = new GroundingMetadata
                        {
                            GroundingChunks = [chunk0, chunk1, chunkUnreferenced],
                            GroundingSupports =
                            [
                                // Spans both streamed segments (0..10 crosses segment 0 [0..6] and segment 1 [6..22])
                                new GroundingSupport
                                {
                                    Segment = new GroundingSegment { StartIndex = 0, EndIndex = 10, Text = "Alpha Beta" },
                                    GroundingChunkIndices = [0, 1]
                                },
                                // Spans single streamed segment (11..16 inside segment 1)
                                new GroundingSupport
                                {
                                    Segment = new GroundingSegment { StartIndex = 11, EndIndex = 16, Text = "Gamma" },
                                    GroundingChunkIndices = []
                                }
                            ]
                        }
                    }
                ]
            },
            "gemini-2.5-flash",
            ref streamedSegments,
            ref cumulativeBytes);

        var carrier = Assert.Single(finalUpdate.Contents, c => c is not TextContent && c.Annotations is { Count: > 0 });
        Assert.Equal(4, carrier.Annotations!.Count);
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

        // 1. Unmodified arguments: reuses rawCall.Args directly and still registers synthesized CallId for FunctionResultContent
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

        // 2. Reordered keys with identical values still reuse rawCall.Args via fallback check
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

        // 6. Duplicate keys in raw wire JSON + caller adding a key must NOT false-positive match rawCall.Args
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

        // 7. Undefined or Null rawCall.Args with null Arguments normalizes FunctionCall.Args to null and passes Validate()
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
    public void TryGetModelContent_NormalizesWithoutDoubleClone_AndPreservesExtensionData()
    {
        using var extDoc = System.Text.Json.JsonDocument.Parse("""{"custom":"meta"}""");
        var originalContent = new Content
        {
            Role = Role.Model,
            ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                ["custom"] = extDoc.RootElement.GetProperty("custom").Clone()
            },
            Parts =
            [
                new Part { Text = "" },
                new Part
                {
                    Text = "",
                    FunctionCall = new FunctionCall { Name = "do_work" }
                },
                Part.FromText("done")
            ]
        };
        var response = new GenerateContentResponse
        {
            Candidates = [new Candidate { Content = originalContent }]
        };

        Assert.True(response.TryGetModelContent(out var normalized));
        Assert.NotSame(originalContent, normalized);
        Assert.Equal(Role.Model, normalized!.Role);
        Assert.Equal(2, normalized.Parts.Count);
        Assert.Null(normalized.Parts[0].Text);
        Assert.Equal("do_work", normalized.Parts[0].FunctionCall?.Name);
        Assert.Equal("done", normalized.Parts[1].Text);
        Assert.Equal("meta", normalized.ExtensionData?["custom"].GetString());

        // Original content remains untouched
        Assert.Equal(3, originalContent.Parts.Count);
        Assert.Equal("", originalContent.Parts[1].Text);
    }

    [Fact]
    public void GenerateContentResponse_TextAndFunctionCalls_DoNotAllocatePartsOrCandidatesListWhenUninitialized()
    {
        var emptyResponse = new GenerateContentResponse();
        Assert.Null(emptyResponse.Text);
        Assert.Empty(emptyResponse.FunctionCalls);
        Assert.Null(emptyResponse.FinishReason);
        Assert.False(emptyResponse.TryGetModelContent(out _));
        _ = ChatResponseMapper.ToChatResponse(emptyResponse, "gemini-2.5-flash");
        _ = ChatResponseMapper.ToChatResponseUpdate(emptyResponse, "gemini-2.5-flash");
        Assert.Empty(emptyResponse.RawCandidates);

        var content = new Content { Role = Role.Model };
        var response = new GenerateContentResponse
        {
            Candidates = [new Candidate { Content = content }]
        };

        Assert.Null(response.Text);
        Assert.Empty(response.FunctionCalls);
        Assert.Empty(content.RawParts);
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
        var mismatchedRequest = new BatchEmbedContentsRequest
        {
            Requests =
            [
                new EmbedContentRequest { Model = "gemini-embedding-001", Content = new Content { Parts = [Part.FromText("a")] } },
                new EmbedContentRequest { Model = "gemini-embedding-001", Content = new Content { Parts = [Part.FromText("b")] } },
                new EmbedContentRequest { Model = "text-embedding-004", Content = new Content { Parts = [Part.FromText("c")] } }
            ]
        };
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.BatchEmbedContentsAsync(mismatchedRequest));
        Assert.Contains("Requests[2]", ex.Message, StringComparison.Ordinal);

        // 3. GeminiEmbeddingGenerator with non-IReadOnlyList<string> enumerable supporting TryGetNonEnumeratedCount (HashSet<string>)
        var generator = client.AsIEmbeddingGenerator();
        var hashSetInputs = new HashSet<string>(StringComparer.Ordinal) { "first", "second", "third" };
        var generated = await generator.GenerateAsync(hashSetInputs);
        Assert.Equal(3, generated.Count);
    }

    [Fact]
    public void ChatResponseMapper_StreamingGrounding_ExplicitPartIndexZeroInMultiPartChunk_ResolvesToPartZero()
    {
        var part0 = Part.FromText("Part 0 text");
        var part1 = Part.FromText("Part 1 text");
        var chunk0 = new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/p0", Title = "Source P0" } };
        var chunk1 = new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/p1", Title = "Source P1" } };

        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [part0, part1]
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks = [chunk0, chunk1],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 6, Text = "Part 0" },
                                GroundingChunkIndices = [0]
                            },
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 1, StartIndex = 0, EndIndex = 6, Text = "Part 1" },
                                GroundingChunkIndices = [1]
                            }
                        ]
                    }
                }
            ]
        };

        List<ChatResponseMapper.StreamedTextSegment>? streamedSegments = null;
        var cumulativeBytes = 0;
        var update = ChatResponseMapper.ToChatResponseUpdate(
            response,
            "gemini-2.5-flash",
            ref streamedSegments,
            ref cumulativeBytes);

        var textContents = update.Contents.OfType<TextContent>().ToList();
        Assert.Equal(2, textContents.Count);

        var p0Citations = textContents[0].Annotations?.OfType<CitationAnnotation>().ToList();
        Assert.NotNull(p0Citations);
        var p0Citation = Assert.Single(p0Citations);
        Assert.Equal("Source P0", p0Citation.Title);
        var p0Region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(p0Citation.AnnotatedRegions!));
        Assert.Equal(0, p0Region.StartIndex);
        Assert.Equal(6, p0Region.EndIndex);

        var p1Citations = textContents[1].Annotations?.OfType<CitationAnnotation>().ToList();
        Assert.NotNull(p1Citations);
        var p1Citation = Assert.Single(p1Citations);
        Assert.Equal("Source P1", p1Citation.Title);
        var p1Region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(p1Citation.AnnotatedRegions!));
        Assert.Equal(0, p1Region.StartIndex);
        Assert.Equal(6, p1Region.EndIndex);
    }

    [Fact]
    public void ChatRequestMapper_ToRequest_MergedTurns_WithReadOnlyPartArray_MergesIntoSingleTurn()
    {
        using var geminiClient = new GeminiClient("test-key", "gemini-2.5-flash");
        var chatClient = geminiClient.AsIChatClient();

        Part[] fixedParts = [Part.FromText("Initial array part")];
        var templateRequest = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = Role.User,
                    Parts = fixedParts
                }
            ]
        };

        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => templateRequest
        };

        ChatMessage[] messages =
        [
            new(ChatRole.User, "Appended message part 1"),
            new(ChatRole.User, "Appended message part 2")
        ];

        var request = ChatRequestMapper.ToRequest(
            messages,
            options,
            chatClient,
            "gemini-2.5-flash",
            System.Text.Json.JsonSerializerOptions.Default);

        // Turn merging must merge into contents[^1] despite fixedParts being a read-only Part[] array,
        // resulting in exactly 1 Content turn with 3 parts rather than 3 separate User turns.
        Assert.Single(request.Contents);
        Assert.Equal(Role.User, request.Contents[0].Role);
        Assert.Equal(3, request.Contents[0].Parts.Count);
        Assert.Equal("Initial array part", request.Contents[0].Parts[0].Text);
        Assert.Equal("Appended message part 1", request.Contents[0].Parts[1].Text);
        Assert.Equal("Appended message part 2", request.Contents[0].Parts[2].Text);
    }

    [Fact]
    public async Task GeminiStreamReader_ReadAsync_StrictlyEnforcesMaxSseEventBytes_RegardlessOfArrayPoolRentSize()
    {
        // Even if ArrayPool rents a buffer of 256 or 4096 bytes, a maxSseEventBytes limit of 50
        // must throw when 51 consecutive bytes without newline are encountered.
        const int maxSseEventBytes = 50;
        var exceedingLine = new string('x', maxSseEventBytes + 1); // 51 bytes
        var bytes = Encoding.UTF8.GetBytes(exceedingLine);
        using var stream = new MemoryStream(bytes);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                stream,
                GeminiStreamingFormat.ServerSentEvents,
                HttpStatusCode.OK,
                watchdog,
                maxSseEventBytes: maxSseEventBytes))
            {
            }
        });

        Assert.Equal(HttpStatusCode.OK, ex.StatusCode);
        Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Conversely, a valid event line within limit must succeed
        var validPayload = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]}}]}";
        var validSse = $"data: {validPayload}\n\n";
        using var validStream = new MemoryStream(Encoding.UTF8.GetBytes(validSse));
        using var validWatchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            validStream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            validWatchdog,
            maxSseEventBytes: 128))
        {
            responses.Add(resp);
        }

        Assert.Single(responses);
        Assert.Equal("ok", responses[0].Text);
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
