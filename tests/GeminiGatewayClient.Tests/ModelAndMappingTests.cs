using System.Reflection;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Http;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Tests;

public class ModelAndMappingTests
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

}
