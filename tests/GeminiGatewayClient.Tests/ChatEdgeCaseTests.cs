using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// Chat mapping edge cases: collections of unusual types and arguments changed between turns.
/// </summary>
public sealed class ChatEdgeCaseTests
{
    private const string DefaultModel = "gemini-2.5-flash";

    #region 1. Pattern 1: 0-based Index Off-by-One Verification

    [Fact]
    public void NonStreaming_MultiCandidateResponse_CandidateIndexZero_MappedToPrimaryMessage()
    {
        var candidate0 = new Candidate
        {
            Index = 0,
            FinishReason = FinishReason.Stop,
            ModelVersion = "model-version-0",
            Content = new Content
            {
                Role = Role.Model,
                Parts = [Part.FromText("Candidate Zero Text")]
            }
        };

        var candidate1 = new Candidate
        {
            Index = 1,
            FinishReason = FinishReason.MaxTokens,
            ModelVersion = "model-version-1",
            Content = new Content
            {
                Role = Role.Model,
                Parts = [Part.FromText("Candidate One Text")]
            }
        };

        var candidate2 = new Candidate
        {
            Index = 2,
            FinishReason = FinishReason.Safety,
            ModelVersion = "model-version-2",
            Content = new Content
            {
                Role = Role.Model,
                Parts = [Part.FromText("Candidate Two Text")]
            }
        };

        var response = new GenerateContentResponse
        {
            ResponseId = "resp-12345",
            Candidates = [candidate0, candidate1, candidate2]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);

        Assert.NotNull(chatResponse);
        Assert.Equal("resp-12345", chatResponse.ResponseId);
        Assert.Equal("model-version-0", chatResponse.ModelId);
        Assert.Equal(ChatFinishReason.Stop, chatResponse.FinishReason);

        var message = Assert.Single(chatResponse.Messages);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Same(candidate0, message.RawRepresentation);
        Assert.Same(response, chatResponse.RawRepresentation);

        var textContent = Assert.IsType<TextContent>(Assert.Single(message.Contents));
        Assert.Equal("Candidate Zero Text", textContent.Text);
    }

    [Fact]
    public void NonStreaming_MultiCandidateResponse_ArrayCollection_PreservesCandidateZero()
    {
        var candidate0 = new Candidate
        {
            Index = 0,
            FinishReason = FinishReason.Stop,
            Content = new Content { Role = Role.Model, Parts = [Part.FromText("Array Candidate 0")] }
        };
        var candidate1 = new Candidate
        {
            Index = 1,
            FinishReason = FinishReason.MaxTokens,
            Content = new Content { Role = Role.Model, Parts = [Part.FromText("Array Candidate 1")] }
        };

        var response = new GenerateContentResponse
        {
            Candidates = new Candidate[] { candidate0, candidate1 }
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        Assert.Equal("Array Candidate 0", chatResponse.Text);
        Assert.Equal(ChatFinishReason.Stop, chatResponse.FinishReason);
        Assert.Same(candidate0, chatResponse.Messages[0].RawRepresentation);
    }

    [Fact]
    public void NonStreaming_ZeroLengthSegment_AsciiText_GeneratesZeroSpanRegion()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("Gemini Gateway Client")] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks = [new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/zero", Title = "Zero Span" } }],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 0, Text = "" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        var tc = Assert.IsType<TextContent>(Assert.Single(chatResponse.Messages[0].Contents));
        var cite = Assert.Single(tc.Annotations!.OfType<CitationAnnotation>());
        var region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cite.AnnotatedRegions!));

        Assert.Equal(0, region.StartIndex);
        Assert.Equal(0, region.EndIndex);
    }

    [Fact]
    public void NonStreaming_ZeroLengthSegment_MidString_GeneratesZeroSpanRegionAtOffset()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("Hello World")] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks = [new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/mid", Title = "Mid Zero Span" } }],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 5, EndIndex = 5, Text = "" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        var tc = Assert.IsType<TextContent>(Assert.Single(chatResponse.Messages[0].Contents));
        var cite = Assert.Single(tc.Annotations!.OfType<CitationAnnotation>());
        var region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cite.AnnotatedRegions!));

        Assert.Equal(5, region.StartIndex);
        Assert.Equal(5, region.EndIndex);
    }

    [Fact]
    public void NonStreaming_ZeroLengthSegment_Utf8MultiByte_GeneratesZeroSpanRegion()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("こんにちは世界！")] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks = [new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/jp", Title = "JP Source" } }],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 0, Text = "" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        var tc = Assert.IsType<TextContent>(Assert.Single(chatResponse.Messages[0].Contents));
        var cite = Assert.Single(tc.Annotations!.OfType<CitationAnnotation>());
        var region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cite.AnnotatedRegions!));

        Assert.Equal(0, region.StartIndex);
        Assert.Equal(0, region.EndIndex);
    }

    [Fact]
    public void NonStreaming_ZeroLengthSegment_CitationMetadata_GeneratesZeroSpanRegion()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("Attributed text")] },
                    CitationMetadata = new CitationMetadata
                    {
                        Citations =
                        [
                            new Citation
                            {
                                Title = "Zero Span Citation",
                                Uri = "https://example.com/cite-zero",
                                StartIndex = 0,
                                EndIndex = 0
                            }
                        ]
                    }
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        var tc = Assert.IsType<TextContent>(Assert.Single(chatResponse.Messages[0].Contents));
        var cite = Assert.Single(tc.Annotations!.OfType<CitationAnnotation>());
        Assert.Equal("Zero Span Citation", cite.Title);
        var region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cite.AnnotatedRegions!));
        Assert.Equal(0, region.StartIndex);
        Assert.Equal(0, region.EndIndex);
    }

    [Fact]
    public void NonStreaming_ZeroLengthSegment_UnattachedGroundingSupport_GeneratesZeroSpanRegion()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("Unattached support text")] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 0, Text = "snippet" }
                            }
                        ]
                    }
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        var tc = Assert.IsType<TextContent>(Assert.Single(chatResponse.Messages[0].Contents));
        var cite = Assert.Single(tc.Annotations!.OfType<CitationAnnotation>());
        Assert.Equal("snippet", cite.Snippet);
        var region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cite.AnnotatedRegions!));
        Assert.Equal(0, region.StartIndex);
        Assert.Equal(0, region.EndIndex);
    }

    #endregion

    #region 2. Pattern 2: Abstract Collection Types Verification

    [Fact]
    public void ChatOptionsMapper_RequestToolsReadOnlyCollection_AppendsNewToolWithoutMutationError()
    {
        var existingTool = new Tool { CodeExecution = new CodeExecutionTool() };
        var readOnlyTools = new ReadOnlyCollection<Tool>(new List<Tool> { existingTool });

        var request = new GenerateContentRequest
        {
            Tools = readOnlyTools
        };

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "order_123", "lookup_order", "Lookup order by id")]
        };

        // Must not throw NotSupportedException when request.Tools is read-only
        ChatOptionsMapper.Apply(request, options);

        Assert.NotNull(request.Tools);
        Assert.NotSame(readOnlyTools, request.Tools);
        Assert.Equal(2, request.Tools.Count);
        Assert.NotNull(request.Tools[0].CodeExecution);
        Assert.NotNull(request.Tools[1].FunctionDeclarations);
        Assert.Equal("lookup_order", request.Tools[1].FunctionDeclarations![0].Name);
    }

    [Fact]
    public void ChatOptionsMapper_RequestToolsArray_AppendsNewToolSuccessfully()
    {
        var existingTool = new Tool { CodeExecution = new CodeExecutionTool() };
        var toolArray = new Tool[] { existingTool };

        var request = new GenerateContentRequest
        {
            Tools = toolArray
        };

        var options = new ChatOptions
        {
            Tools = [new HostedWebSearchTool()]
        };

        // Tool[] has IsReadOnly == true; must not throw when appending
        ChatOptionsMapper.Apply(request, options);

        Assert.NotNull(request.Tools);
        Assert.NotSame(toolArray, request.Tools);
        Assert.Equal(2, request.Tools.Count);
        Assert.NotNull(request.Tools.FirstOrDefault(t => t.CodeExecution != null));
        Assert.NotNull(request.Tools.FirstOrDefault(t => t.GoogleSearch != null));
    }

    [Fact]
    public void ChatOptionsMapper_ReadOnlyFunctionDeclarations_ReplacesAndAppendsDeclaration()
    {
        var existingFunc = new FunctionDeclaration { Name = "fn_one" };
        var readOnlyDecls = new ReadOnlyCollection<FunctionDeclaration>(new List<FunctionDeclaration> { existingFunc });
        var existingTool = new Tool { FunctionDeclarations = readOnlyDecls };

        var request = new GenerateContentRequest
        {
            Tools = [existingTool]
        };

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "res2", "fn_two")]
        };

        ChatOptionsMapper.Apply(request, options);

        Assert.Equal(["fn_one", "fn_two"], Assert.Single(request.Tools!).FunctionDeclarations!.Select(d => d.Name));
        Assert.Same(readOnlyDecls, existingTool.FunctionDeclarations);
    }

    [Fact]
    public void ChatOptionsMapper_ArrayFunctionDeclarations_AppendsDeclaration()
    {
        var existingFunc = new FunctionDeclaration { Name = "fn_one" };
        var declArray = new FunctionDeclaration[] { existingFunc };
        var existingTool = new Tool { FunctionDeclarations = declArray };

        var request = new GenerateContentRequest
        {
            Tools = [existingTool]
        };

        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "res2", "fn_two")]
        };

        ChatOptionsMapper.Apply(request, options);

        Assert.Equal(["fn_one", "fn_two"], Assert.Single(request.Tools!).FunctionDeclarations!.Select(d => d.Name));
        Assert.Same(declArray, existingTool.FunctionDeclarations);
    }

    [Fact]
    public void ChatOptionsMapper_ToolSharedBetweenRequests_KeepsOnlyTheFunctionsOfEachCall()
    {
        var shared = new Tool { FunctionDeclarations = [new FunctionDeclaration { Name = "fn_shared" }], UrlContext = new UrlContextTool() };

        foreach (var name in new[] { "fn_first", "fn_second" })
        {
            var request = new GenerateContentRequest { Tools = [shared] };

            ChatOptionsMapper.Apply(request, new ChatOptions { Tools = [AIFunctionFactory.Create(() => "res", name)] });

            var sent = Assert.Single(request.Tools!);
            Assert.Equal(["fn_shared", name], sent.FunctionDeclarations!.Select(d => d.Name));
            Assert.Same(shared.UrlContext, sent.UrlContext);
        }

        Assert.Equal("fn_shared", Assert.Single(shared.FunctionDeclarations).Name);
    }

    [Fact]
    public void ChatOptionsMapper_OptionsToolsReadOnlyCollection_MapsAllTools()
    {
        var options = new ChatOptions
        {
            Tools = new ReadOnlyCollection<AITool>(
            [
                AIFunctionFactory.Create(() => "ro", "fn_readonly"),
                new HostedWebSearchTool()
            ])
        };

        var request = new GenerateContentRequest();
        ChatOptionsMapper.Apply(request, options);

        Assert.NotNull(request.Tools);
        Assert.Equal(2, request.Tools.Count);
        Assert.Equal("fn_readonly", request.Tools[0].FunctionDeclarations![0].Name);
        Assert.NotNull(request.Tools[1].GoogleSearch);
    }

    [Fact]
    public void ChatOptionsMapper_OptionsToolsArray_MapsAllTools()
    {
        var options = new ChatOptions
        {
            Tools = new AITool[]
            {
                AIFunctionFactory.Create(() => "arr", "fn_array"),
                new HostedCodeInterpreterTool()
            }
        };

        var request = new GenerateContentRequest();
        ChatOptionsMapper.Apply(request, options);

        Assert.NotNull(request.Tools);
        Assert.Equal(2, request.Tools.Count);
        Assert.Equal("fn_array", request.Tools[0].FunctionDeclarations![0].Name);
        Assert.NotNull(request.Tools[1].CodeExecution);
    }

    [Fact]
    public void ChatOptionsMapper_SameFunctionNameTwiceInOptions_IsDeclaredOnce()
    {
        var first = AIFunctionFactory.Create(() => "a", "fn_dup", "first");
        var options = new ChatOptions
        {
            Tools = [first, AIFunctionFactory.Create(() => "b", "fn_dup", "second"), AIFunctionFactory.Create(() => "c", "fn_other")]
        };

        var request = new GenerateContentRequest();
        ChatOptionsMapper.Apply(request, options);

        var declarations = Assert.Single(request.Tools!).FunctionDeclarations!;
        Assert.Equal(["fn_dup", "fn_other"], declarations.Select(d => d.Name));
        Assert.Equal("first", declarations[0].Description);
    }

    [Fact]
    public void ChatOptionsMapper_SameFunctionNameTwiceInOptions_NextToATemplateTool_IsDeclaredOnce()
    {
        var request = new GenerateContentRequest { Tools = [new Tool { FunctionDeclarations = [new FunctionDeclaration { Name = "fn_template" }] }] };
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "a", "fn_dup"), AIFunctionFactory.Create(() => "b", "fn_dup"), AIFunctionFactory.Create(() => "c", "fn_template")]
        };

        ChatOptionsMapper.Apply(request, options);

        Assert.Equal(["fn_template", "fn_dup"], Assert.Single(request.Tools!).FunctionDeclarations!.Select(d => d.Name));
    }

    [Fact]
    public void ChatOptionsMapper_NullToolInOptions_ThrowsArgumentException()
    {
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "a", "fn_ok"), null!] };

        var ex = Assert.Throws<ArgumentException>(() => ChatOptionsMapper.Apply(new GenerateContentRequest(), options));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("Tools[1]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IChatClient_NullToolInOptions_ThrowsArgumentExceptionBeforeSending()
    {
        using var handler = new RecordingHandler("""{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""");
        using var client = new GeminiClient(new GeminiClientOptions { ApiKey = "k", Model = DefaultModel }, new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.AsIChatClient().GetResponseAsync("hi", new ChatOptions { Tools = [null!] }));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public void ChatMessageContents_CustomNonListEnumerable_MapsAllParts()
    {
        var customContents = new CustomAIContentCollection(
        [
            new TextContent("Hello custom non-list"),
            new DataContent(new byte[] { 1, 2, 3, 4 }, "image/png"),
            new UriContent(new Uri("https://example.com/audio.mp3"), "audio/mp3")
        ]);

        var message = new ChatMessage(ChatRole.User, customContents);
        var request = ChatRequestMapper.ToRequest([message], options: null, client: null!, JsonSerializerOptions.Default);

        Assert.Single(request.Contents);
        var parts = request.Contents[0].Parts;
        Assert.Equal(3, parts.Count);

        Assert.Equal("Hello custom non-list", parts[0].Text);
        Assert.NotNull(parts[1].InlineData);
        Assert.Equal("image/png", parts[1].InlineData!.MimeType);
        Assert.NotNull(parts[2].FileData);
        Assert.Equal("https://example.com/audio.mp3", parts[2].FileData!.FileUri);
    }

    [Fact]
    public void ChatMessageContents_CustomReadOnlyCollection_ConsecutiveMessagesMerged()
    {
        var msg1Contents = new CustomReadOnlyAIContentCollection([new TextContent("Message 1 text")]);
        var msg2Contents = new CustomReadOnlyAIContentCollection([new TextContent("Message 2 text")]);

        var msg1 = new ChatMessage(ChatRole.User, msg1Contents);
        var msg2 = new ChatMessage(ChatRole.User, msg2Contents);

        var request = ChatRequestMapper.ToRequest([msg1, msg2], options: null, client: null!, JsonSerializerOptions.Default);

        // Consecutive User messages should be merged into a single Content object
        Assert.Single(request.Contents);
        var parts = request.Contents[0].Parts;
        Assert.Equal(2, parts.Count);
        Assert.Equal("Message 1 text", parts[0].Text);
        Assert.Equal("Message 2 text", parts[1].Text);
    }

    [Fact]
    public void ChatRequestMapper_LazyMessagesEnumerable_MapsAllMessages()
    {
        static IEnumerable<ChatMessage> CreateLazyMessages()
        {
            yield return new ChatMessage(ChatRole.User, "User prompt 1");
            yield return new ChatMessage(ChatRole.Assistant, "Model reply 1");
            yield return new ChatMessage(ChatRole.User, "User prompt 2");
        }

        var request = ChatRequestMapper.ToRequest(CreateLazyMessages(), options: null, client: null!, JsonSerializerOptions.Default);

        Assert.Equal(3, request.Contents.Count);
        Assert.Equal(Role.User, request.Contents[0].Role);
        Assert.Equal("User prompt 1", request.Contents[0].Parts[0].Text);
        Assert.Equal(Role.Model, request.Contents[1].Role);
        Assert.Equal("Model reply 1", request.Contents[1].Parts[0].Text);
        Assert.Equal(Role.User, request.Contents[2].Role);
        Assert.Equal("User prompt 2", request.Contents[2].Parts[0].Text);
    }

    #endregion

    #region 3. Pattern 5: Mutation Safety on Cache Reuse Verification

    [Fact]
    public void ReplayedArguments_CaseInsensitiveKeyMismatch_ReturnsFalseAndReSerializesWithUserCasing()
    {
        using var rawDoc = JsonDocument.Parse("""{"city":"Tokyo"}""");
        var rawArgs = rawDoc.RootElement.Clone();

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
                                FunctionCall = new FunctionCall
                                {
                                    Name = "get_weather",
                                    Args = rawArgs
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var mappedChat = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel);
        var assistantMsg = mappedChat.Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // User replaces Arguments with a case-insensitive dictionary using uppercase key "CITY"
        var caseInsensitiveArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CITY"] = "Tokyo"
        };
        fnCall.Arguments = caseInsensitiveArgs;

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);

        // When casing differs, cache MUST NOT be reused; wire must reflect user's "CITY" property
        Assert.True(replayedCall.Args.Value.TryGetProperty("CITY", out var cityProp));
        Assert.Equal("Tokyo", cityProp.GetString());
        Assert.False(replayedCall.Args.Value.TryGetProperty("city", out _));
    }

    [Fact]
    public void ReplayedArguments_DynamicallyAddedArgument_BypassesCacheAndSerializesAllArguments()
    {
        using var rawDoc = JsonDocument.Parse("""{"city":"Tokyo"}""");
        var rawArgs = rawDoc.RootElement.Clone();

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
                                FunctionCall = new FunctionCall
                                {
                                    Name = "get_weather",
                                    Args = rawArgs
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var mappedChat = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel);
        var assistantMsg = mappedChat.Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Dynamically add a new argument
        fnCall.Arguments!["units"] = "metric";

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);

        // Bypasses cache: both keys must exist
        Assert.True(replayedCall.Args.Value.TryGetProperty("city", out var cityProp));
        Assert.Equal("Tokyo", cityProp.GetString());
        Assert.True(replayedCall.Args.Value.TryGetProperty("units", out var unitsProp));
        Assert.Equal("metric", unitsProp.GetString());
    }

    [Fact]
    public void ReplayedArguments_DynamicallyAddedJsonElementArgument_BypassesCache()
    {
        using var rawDoc = JsonDocument.Parse("""{"city":"Tokyo"}""");
        var rawArgs = rawDoc.RootElement.Clone();

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
                                FunctionCall = new FunctionCall
                                {
                                    Name = "get_weather",
                                    Args = rawArgs
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var mappedChat = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel);
        var assistantMsg = mappedChat.Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Dynamically add a JsonElement argument
        fnCall.Arguments!["limit"] = JsonSerializer.SerializeToElement(10);

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);

        Assert.True(replayedCall.Args.Value.TryGetProperty("limit", out var limitProp));
        Assert.Equal(10, limitProp.GetInt32());
        Assert.True(replayedCall.Args.Value.TryGetProperty("city", out var cityProp));
        Assert.Equal("Tokyo", cityProp.GetString());
    }

    [Fact]
    public void ReplayedArguments_MutatedJsonElementValue_BypassesCache()
    {
        using var rawDoc = JsonDocument.Parse("""{"city":"Tokyo"}""");
        var rawArgs = rawDoc.RootElement.Clone();

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
                                FunctionCall = new FunctionCall
                                {
                                    Name = "get_weather",
                                    Args = rawArgs
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var mappedChat = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel);
        var assistantMsg = mappedChat.Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Mutate the value using another JsonElement
        fnCall.Arguments!["city"] = JsonSerializer.SerializeToElement("Kyoto");

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);
        Assert.Equal("Kyoto", replayedCall.Args.Value.GetProperty("city").GetString());
    }

    [Fact]
    public void ReplayedArguments_MutatedClrObjectValue_BypassesCache()
    {
        using var rawDoc = JsonDocument.Parse("""{"city":"Tokyo"}""");
        var rawArgs = rawDoc.RootElement.Clone();

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
                                FunctionCall = new FunctionCall
                                {
                                    Name = "get_weather",
                                    Args = rawArgs
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var mappedChat = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel);
        var assistantMsg = mappedChat.Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Mutate with a plain CLR string (not JsonElement)
        fnCall.Arguments!["city"] = "Osaka";

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);
        Assert.Equal("Osaka", replayedCall.Args.Value.GetProperty("city").GetString());
    }

    [Fact]
    public void ReplayedArguments_RemovedArgument_BypassesCache()
    {
        using var rawDoc = JsonDocument.Parse("""{"city":"Tokyo","units":"metric"}""");
        var rawArgs = rawDoc.RootElement.Clone();

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
                                FunctionCall = new FunctionCall
                                {
                                    Name = "get_weather",
                                    Args = rawArgs
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var mappedChat = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel);
        var assistantMsg = mappedChat.Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Remove argument
        Assert.True(fnCall.Arguments!.Remove("units"));

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);
        Assert.True(replayedCall.Args.Value.TryGetProperty("city", out var cityProp));
        Assert.Equal("Tokyo", cityProp.GetString());
        Assert.False(replayedCall.Args.Value.TryGetProperty("units", out _));
    }

    [Fact]
    public void ReplayedArguments_UnmodifiedArguments_ReusesCachedRawArgs()
    {
        using var rawDoc = JsonDocument.Parse("""{"city":"Tokyo","units":"metric"}""");
        var rawArgs = rawDoc.RootElement.Clone();

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
                                FunctionCall = new FunctionCall
                                {
                                    Name = "get_weather",
                                    Args = rawArgs
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var mappedChat = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel);
        var assistantMsg = mappedChat.Messages[0];

        // Call ToRequest without modifying Arguments
        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);
        Assert.True(JsonElement.DeepEquals(rawArgs, replayedCall.Args.Value));
    }

    #endregion

    #region Helper Types

    private sealed class CustomAIContentCollection : IList<AIContent>, IReadOnlyList<AIContent>
    {
        private readonly List<AIContent> _items;

        public CustomAIContentCollection(IEnumerable<AIContent> items) => _items = [.. items];

        public AIContent this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public int Count => _items.Count;
        public bool IsReadOnly => false;

        public void Add(AIContent item) => _items.Add(item);
        public void Clear() => _items.Clear();
        public bool Contains(AIContent item) => _items.Contains(item);
        public void CopyTo(AIContent[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public IEnumerator<AIContent> GetEnumerator() => _items.GetEnumerator();
        public int IndexOf(AIContent item) => _items.IndexOf(item);
        public void Insert(int index, AIContent item) => _items.Insert(index, item);
        public bool Remove(AIContent item) => _items.Remove(item);
        public void RemoveAt(int index) => _items.RemoveAt(index);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CustomReadOnlyAIContentCollection : IList<AIContent>, IReadOnlyList<AIContent>
    {
        private readonly List<AIContent> _items;

        public CustomReadOnlyAIContentCollection(IEnumerable<AIContent> items) => _items = [.. items];

        public AIContent this[int index]
        {
            get => _items[index];
            set => throw new NotSupportedException();
        }

        public int Count => _items.Count;
        public bool IsReadOnly => true;

        public void Add(AIContent item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(AIContent item) => _items.Contains(item);
        public void CopyTo(AIContent[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public IEnumerator<AIContent> GetEnumerator() => _items.GetEnumerator();
        public int IndexOf(AIContent item) => _items.IndexOf(item);
        public void Insert(int index, AIContent item) => throw new NotSupportedException();
        public bool Remove(AIContent item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    #endregion
}
