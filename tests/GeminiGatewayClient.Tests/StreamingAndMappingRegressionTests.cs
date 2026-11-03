using System.Net;
using System.Text;
using System.Text.Json;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Http;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

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
}
