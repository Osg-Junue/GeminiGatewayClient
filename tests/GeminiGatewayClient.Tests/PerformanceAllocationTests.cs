using System.Reflection;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Http;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Tests;

public class PerformanceAllocationTests
{
    [Fact]
    public void ChatResponseMapper_EmptyOrSinglePart_MapsExpectedContents()
    {
        var uninitializedContent = new Content { Role = Role.Model };
        var uninitializedResponse = new GenerateContentResponse
        {
            Candidates = [new Candidate { Content = uninitializedContent }]
        };

        var mappedUninitialized = ChatResponseMapper.ToChatResponse(uninitializedResponse, "gemini-2.5-flash");
        var uninitializedContents = Assert.IsAssignableFrom<IList<AIContent>>(mappedUninitialized.Messages[0].Contents);
        Assert.Empty(uninitializedContents);
        Assert.Empty(uninitializedContent.Parts);

        var singlePartResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [Part.FromText("hello")]
                    }
                }
            ]
        };

        var mappedSingle = ChatResponseMapper.ToChatResponse(singlePartResponse, "gemini-2.5-flash");
        var singleContents = Assert.IsAssignableFrom<IList<AIContent>>(mappedSingle.Messages[0].Contents);
        Assert.Single(singleContents);
    }

    [Fact]
    public void ChatOptionsMapper_ApplyTools_GroupsDeclarationsIntoOneFunctionTool()
    {
        var functionsOnlyOptions = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create((string a) => a, "fn1"),
                AIFunctionFactory.Create((string a) => a, "fn2"),
                AIFunctionFactory.Create((string a) => a, "fn3"),
                AIFunctionFactory.Create((string a) => a, "fn4"),
                AIFunctionFactory.Create((string a) => a, "fn5"),
                AIFunctionFactory.Create((string a) => a, "fn6")
            ]
        };

        var request = new GenerateContentRequest();
        ChatOptionsMapper.Apply(request, functionsOnlyOptions, "gemini-2.5-flash");

        var tools = Assert.IsAssignableFrom<IList<Tool>>(request.Tools);
        Assert.Single(tools);

        var declarations = Assert.IsAssignableFrom<IList<FunctionDeclaration>>(tools[0].FunctionDeclarations);
        Assert.Equal(6, declarations.Count);

        // Mixed function declarations + HostedWebSearchTool should not resize mappedTools beyond 2
        var mixedOptions = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create((string a) => a, "fn1"),
                AIFunctionFactory.Create((string a) => a, "fn2"),
                new HostedWebSearchTool()
            ]
        };

        var mixedRequest = new GenerateContentRequest();
        ChatOptionsMapper.Apply(mixedRequest, mixedOptions, "gemini-2.5-flash");

        var mixedTools = Assert.IsAssignableFrom<IList<Tool>>(mixedRequest.Tools);
        Assert.Equal(2, mixedTools.Count);
        Assert.NotNull(mixedTools[0].FunctionDeclarations);
        Assert.NotNull(mixedTools[1].GoogleSearch);
    }

    [Fact]
    public void ChatRequestMapper_ToRequest_MapsContentsAndParts_AndOmitsUnusedSystemParts()
    {
        using var geminiClient = new GeminiClient("test-key", "gemini-2.5-flash");
        var chatClient = geminiClient.AsIChatClient();

        ChatMessage[] messages =
        [
            new(ChatRole.User, "Turn 1"),
            new(ChatRole.Assistant, "Turn 2"),
            new(ChatRole.User, "Turn 3"),
            new(ChatRole.Assistant, "Turn 4"),
            new(ChatRole.User, "Turn 5")
        ];

        var request = ChatRequestMapper.ToRequest(messages, options: null, chatClient, "gemini-2.5-flash", AIJsonUtilities.DefaultOptions);

        Assert.Null(request.SystemInstruction);
        var contents = Assert.IsAssignableFrom<IList<Content>>(request.Contents);
        Assert.Equal(5, contents.Count);

        foreach (var content in contents)
        {
            var parts = Assert.IsAssignableFrom<IList<Part>>(content.Parts);
            Assert.Single(parts);
        }
    }

    [Fact]
    public void ChatRequestMapper_ToRequest_CombinedInstructionsAndSystemMessage_AndMergedTurns_WorkCleanly()
    {
        using var geminiClient = new GeminiClient("test-key", "gemini-2.5-flash");
        var chatClient = geminiClient.AsIChatClient();

        ChatMessage[] messages =
        [
            new(ChatRole.System, "System message 2"),
            new(ChatRole.User, "Question"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "fn"), new FunctionCallContent("c2", "fn")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "r1")]),
            new(ChatRole.Tool, [new FunctionResultContent("c2", "r2")])
        ];

        var options = new ChatOptions { Instructions = "System message 1" };
        var request = ChatRequestMapper.ToRequest(messages, options, chatClient, "gemini-2.5-flash", AIJsonUtilities.DefaultOptions);

        Assert.NotNull(request.SystemInstruction);
        var systemParts = Assert.IsAssignableFrom<IList<Part>>(request.SystemInstruction!.Parts);
        Assert.Equal(2, systemParts.Count);

        // Two consecutive Tool messages are merged into a single User turn with 2 FunctionResponse parts
        Assert.Equal(3, request.Contents.Count);
        Assert.Equal(2, request.Contents[2].Parts.Count);
    }

    [Fact]
    public void GeminiRequestHeaders_Create_AppliesConfiguredStaticAndAdditionalHeaders()
    {
        var options = new GeminiClientOptions
        {
            ApiKey = "secret-key",
            AdditionalHeaders =
            {
                ["X-Custom-1"] = "v1",
                ["X-Custom-2"] = "v2"
            }
        };

        var headers = GeminiRequestHeaders.Create(options);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://example.com");
        var task = headers.ApplyAsync(message, CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully);

        Assert.Equal("secret-key", Assert.Single(message.Headers.GetValues(GeminiClientOptions.DefaultApiKeyHeader)));
        Assert.Equal("v1", Assert.Single(message.Headers.GetValues("X-Custom-1")));
        Assert.Equal("v2", Assert.Single(message.Headers.GetValues("X-Custom-2")));
    }

    [Fact]
    public void ChatResponseMapper_WarmPath_AllocatesWithinExpectedBudget()
    {
        var chunk = new GenerateContentResponse
        {
            ResponseId = "r1",
            ModelVersion = "gemini-2.5-flash",
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [Part.FromText("chunk")]
                    }
                }
            ]
        };

        // Warmup JIT
        for (var i = 0; i < 50; i++)
        {
            _ = ChatResponseMapper.ToChatResponseUpdate(chunk, "gemini-2.5-flash");
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = ChatResponseMapper.ToChatResponseUpdate(chunk, "gemini-2.5-flash");
        }

        var allocatedPerCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // ~296 B in 64-bit Release; allow up to 400 B for Debug/coverage/PGO instrumentation headroom.
        Assert.InRange(allocatedPerCall, 1, 400);
    }

    [Fact]
    public void ModelCollections_LazilyInitializeNonNullEmptyListsOnGetterAccess_IncludingAfterNullAssignment()
    {
        var emptyContent = new Content();
        var clonedEmpty = emptyContent.Clone();
        Assert.Empty(emptyContent.Parts);
        Assert.Empty(clonedEmpty.Parts);
        emptyContent.Parts = null!;
        Assert.Empty(emptyContent.Parts);

        var emptyRequest = new GenerateContentRequest();
        Assert.Empty(emptyRequest.Contents);
        emptyRequest.Contents = null!;
        Assert.Empty(emptyRequest.Contents);

        var emptyResponse = new GenerateContentResponse();
        Assert.Null(emptyResponse.Text);
        Assert.Empty(emptyResponse.FunctionCalls);
        Assert.Null(emptyResponse.FinishReason);
        Assert.Empty(emptyResponse.Candidates);
        emptyResponse.Candidates = null!;
        Assert.Empty(emptyResponse.Candidates);

        var citationMeta = new CitationMetadata();
        Assert.Empty(citationMeta.Citations);
        citationMeta.Citations = null!;
        Assert.Empty(citationMeta.Citations);

        var batchEmbedReq = new BatchEmbedContentsRequest();
        Assert.Empty(batchEmbedReq.Requests);
        batchEmbedReq.Requests = null!;
        Assert.Empty(batchEmbedReq.Requests);

        var batchEmbedResp = new BatchEmbedContentsResponse();
        Assert.Empty(batchEmbedResp.Embeddings);
        batchEmbedResp.Embeddings = null!;
        Assert.Empty(batchEmbedResp.Embeddings);

        var embedding = new ContentEmbedding();
        Assert.Empty(embedding.Values);
        embedding.Values = null!;
        Assert.Empty(embedding.Values);

        var grounding = new GroundingMetadata();
        Assert.Empty(grounding.WebSearchQueries);
        Assert.Empty(grounding.GroundingChunks);
        Assert.Empty(grounding.GroundingSupports);
        grounding.WebSearchQueries = null!;
        grounding.GroundingChunks = null!;
        grounding.GroundingSupports = null!;
        Assert.Empty(grounding.WebSearchQueries);
        Assert.Empty(grounding.GroundingChunks);
        Assert.Empty(grounding.GroundingSupports);

        var support = new GroundingSupport();
        Assert.Empty(support.GroundingChunkIndices);
        support.GroundingChunkIndices = null!;
        Assert.Empty(support.GroundingChunkIndices);
    }

    [Theory]
    [InlineData(typeof(Content))]
    [InlineData(typeof(GenerateContentRequest))]
    [InlineData(typeof(GenerateContentResponse))]
    [InlineData(typeof(CitationMetadata))]
    [InlineData(typeof(BatchEmbedContentsRequest))]
    [InlineData(typeof(BatchEmbedContentsResponse))]
    [InlineData(typeof(ContentEmbedding))]
    [InlineData(typeof(GroundingMetadata))]
    [InlineData(typeof(GroundingSupport))]
    public void ModelCollections_AreNotAllocatedByTheConstructor(Type modelType)
    {
        var model = Activator.CreateInstance(modelType)!;

        var collectionFields = modelType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(field.FieldType))
            .ToList();

        Assert.NotEmpty(collectionFields);
        Assert.All(collectionFields, field => Assert.Null(field.GetValue(model)));
    }

    [Fact]
    public void GenerateContentRequest_Validate_AllocationDoesNotGrowWithHistoryLength()
    {
        static GenerateContentRequest CreateHistory(int count)
        {
            var contents = new List<Content>(count);
            for (var i = 0; i < count; i++)
            {
                contents.Add(new Content { Role = i % 2 == 0 ? Role.User : Role.Model, Parts = [Part.FromText("turn"), Part.FromText("more")] });
            }

            return new GenerateContentRequest { Contents = contents };
        }

        static long MeasureValidate(GenerateContentRequest request)
        {
            for (var i = 0; i < 50; i++)
            {
                request.Validate();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                request.Validate();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        }

        const int turns = 256;
        var shortHistory = MeasureValidate(CreateHistory(1));
        var longHistory = MeasureValidate(CreateHistory(turns));

        // Under one byte per turn: a per-turn string costs hundreds, while a stray one-off runtime allocation can land in the window.
        Assert.True(longHistory - shortHistory < turns, $"1 turn allocated {shortHistory} B, {turns} turns allocated {longHistory} B.");
    }

    [Fact]
    public void BatchEmbedContentsRequest_Validate_AllocationDoesNotGrowWithBatchSize()
    {
        static long MeasureValidate(int count)
        {
            var requests = new List<EmbedContentRequest>(count);
            for (var i = 0; i < count; i++)
            {
                requests.Add(EmbedContentRequest.FromText("document"));
            }

            var batch = new BatchEmbedContentsRequest { Requests = requests };
            for (var i = 0; i < 50; i++)
            {
                batch.Validate();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                batch.Validate();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        }

        const int items = 256;
        var single = MeasureValidate(1);
        var large = MeasureValidate(items);

        Assert.True(large - single < items, $"1 item allocated {single} B, {items} items allocated {large} B.");
    }

    [Fact]
    public void Schema_Validate_AllocationDoesNotGrowWithPropertyCount()
    {
        static long MeasureValidate(int propertyCount)
        {
            var properties = new Dictionary<string, Schema>(propertyCount);
            for (var i = 0; i < propertyCount; i++)
            {
                properties[$"property{i}"] = new Schema { Type = SchemaType.String };
            }

            var config = new GenerationConfig { ResponseSchema = new Schema { Type = SchemaType.Object, Properties = properties } };
            for (var i = 0; i < 50; i++)
            {
                config.Validate();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                config.Validate();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        }

        const int properties = 256;
        var single = MeasureValidate(1);
        var large = MeasureValidate(properties);

        Assert.True(large - single < properties, $"1 property allocated {single} B, {properties} properties allocated {large} B.");
    }
}
