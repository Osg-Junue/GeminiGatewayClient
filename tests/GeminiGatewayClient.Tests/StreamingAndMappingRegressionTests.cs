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
}
