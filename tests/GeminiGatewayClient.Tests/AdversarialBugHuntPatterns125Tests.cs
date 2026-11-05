using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// Adversarial empirical tests targeting Bug Patterns 1, 2, and 5:
/// - Pattern 1 (0-based Index Off-by-one): non-streaming ChatResponseMapper.ToChatResponse with PartIndex = 0,
///   multi-part candidates, multi-candidate responses (Candidate.Index = 0), and zero-length text segments.
/// - Pattern 2 (Abstract Collection Types): ChatOptionsMapper.Apply when request.Tools is ReadOnlyCollection or array;
///   ChatMessage.Contents when passed as custom non-list IEnumerable&lt;AIContent&gt;.
/// - Pattern 5 (Mutation Safety on Cache Reuse): ChatRequestMapper.AreUnmodifiedArguments with case-insensitive
///   keys ("CITY" vs wire "city"), dynamically added arguments, and mutated arguments.
/// </summary>
public sealed class AdversarialBugHuntPatterns125Tests
{
    private const string DefaultModel = "gemini-2.5-flash";

    private static readonly MethodInfo AreUnmodifiedArgumentsMethod = typeof(ChatRequestMapper)
        .GetMethod("AreUnmodifiedArguments", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool InvokeAreUnmodifiedArguments(IDictionary<string, object?> arguments, JsonElement? rawArgs) =>
        (bool)AreUnmodifiedArgumentsMethod.Invoke(null, [arguments, rawArgs])!;

    #region 1. Pattern 1: 0-based Index Off-by-One Verification

    [Fact]
    public void Pattern1_NonStreaming_MultiPartCandidate_PartIndexZero_AttachesGroundingToPartZeroOnly()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts =
                        [
                            Part.FromText("First paragraph content."),
                            Part.FromText("Second paragraph content."),
                            Part.FromText("Third paragraph content.")
                        ]
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/p0", Title = "Source P0" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 5, Text = "First" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        var textContents = chatResponse.Messages[0].Contents.OfType<TextContent>().ToList();

        Assert.Equal(3, textContents.Count);

        // Part 0 (Index 0) MUST have the grounding citation
        var p0Annotations = textContents[0].Annotations?.OfType<CitationAnnotation>().ToList();
        Assert.NotNull(p0Annotations);
        var p0Cite = Assert.Single(p0Annotations);
        Assert.Equal("Source P0", p0Cite.Title);
        Assert.Equal(new Uri("https://example.com/p0"), p0Cite.Url);
        var p0Region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(p0Cite.AnnotatedRegions!));
        Assert.Equal(0, p0Region.StartIndex);
        Assert.Equal(5, p0Region.EndIndex);

        // Part 1 and Part 2 must NOT have annotations from Part 0
        Assert.Null(textContents[1].Annotations);
        Assert.Null(textContents[2].Annotations);
    }

    [Fact]
    public void Pattern1_NonStreaming_MultiPartCandidate_PartIndexZeroAndNonZero_ResolveIndependently()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts =
                        [
                            Part.FromText("Part0 text"),
                            Part.FromText("Part1 text"),
                            Part.FromText("Part2 text")
                        ]
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/0", Title = "Chunk 0" } },
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/1", Title = "Chunk 1" } },
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/2", Title = "Chunk 2" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 5, Text = "Part0" },
                                GroundingChunkIndices = [0]
                            },
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 1, StartIndex = 0, EndIndex = 5, Text = "Part1" },
                                GroundingChunkIndices = [1]
                            },
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 2, StartIndex = 0, EndIndex = 5, Text = "Part2" },
                                GroundingChunkIndices = [2]
                            }
                        ]
                    }
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        var textContents = chatResponse.Messages[0].Contents.OfType<TextContent>().ToList();
        Assert.Equal(3, textContents.Count);

        // Verify Part 0 -> Chunk 0
        var p0Cite = Assert.Single(textContents[0].Annotations!.OfType<CitationAnnotation>());
        Assert.Equal("Chunk 0", p0Cite.Title);
        var r0 = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(p0Cite.AnnotatedRegions!));
        Assert.Equal(0, r0.StartIndex);
        Assert.Equal(5, r0.EndIndex);

        // Verify Part 1 -> Chunk 1
        var p1Cite = Assert.Single(textContents[1].Annotations!.OfType<CitationAnnotation>());
        Assert.Equal("Chunk 1", p1Cite.Title);
        var r1 = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(p1Cite.AnnotatedRegions!));
        Assert.Equal(0, r1.StartIndex);
        Assert.Equal(5, r1.EndIndex);

        // Verify Part 2 -> Chunk 2
        var p2Cite = Assert.Single(textContents[2].Annotations!.OfType<CitationAnnotation>());
        Assert.Equal("Chunk 2", p2Cite.Title);
        var r2 = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(p2Cite.AnnotatedRegions!));
        Assert.Equal(0, r2.StartIndex);
        Assert.Equal(5, r2.EndIndex);
    }

    [Fact]
    public void Pattern1_NonStreaming_MultiCandidateResponse_CandidateIndexZero_MappedToPrimaryMessage()
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
    public void Pattern1_NonStreaming_MultiCandidateResponse_ArrayCollection_PreservesCandidateZero()
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
    public void Pattern1_NonStreaming_ZeroLengthSegment_AsciiText_GeneratesZeroSpanRegion()
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
    public void Pattern1_NonStreaming_ZeroLengthSegment_MidString_GeneratesZeroSpanRegionAtOffset()
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
    public void Pattern1_NonStreaming_ZeroLengthSegment_Utf8MultiByte_GeneratesZeroSpanRegion()
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
    public void Pattern1_NonStreaming_ZeroLengthSegment_EmptyText_GeneratesZeroSpanRegion()
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("")] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks = [new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/empty", Title = "Empty Source" } }],
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
        Assert.Equal("", tc.Text);
        var cite = Assert.Single(tc.Annotations!.OfType<CitationAnnotation>());
        var region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cite.AnnotatedRegions!));

        Assert.Equal(0, region.StartIndex);
        Assert.Equal(0, region.EndIndex);
    }

    [Fact]
    public void Pattern1_NonStreaming_ZeroLengthSegment_CitationMetadata_GeneratesZeroSpanRegion()
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
    public void Pattern1_NonStreaming_ZeroLengthSegment_UnattachedGroundingSupport_GeneratesZeroSpanRegion()
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
    public void Pattern2_ChatOptionsMapper_RequestToolsReadOnlyCollection_AppendsNewToolWithoutMutationError()
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
        ChatOptionsMapper.Apply(request, options, DefaultModel);

        Assert.NotNull(request.Tools);
        Assert.NotSame(readOnlyTools, request.Tools);
        Assert.Equal(2, request.Tools.Count);
        Assert.NotNull(request.Tools[0].CodeExecution);
        Assert.NotNull(request.Tools[1].FunctionDeclarations);
        Assert.Equal("lookup_order", request.Tools[1].FunctionDeclarations![0].Name);
    }

    [Fact]
    public void Pattern2_ChatOptionsMapper_RequestToolsArray_AppendsNewToolSuccessfully()
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
        ChatOptionsMapper.Apply(request, options, DefaultModel);

        Assert.NotNull(request.Tools);
        Assert.NotSame(toolArray, request.Tools);
        Assert.Equal(2, request.Tools.Count);
        Assert.NotNull(request.Tools.FirstOrDefault(t => t.CodeExecution != null));
        Assert.NotNull(request.Tools.FirstOrDefault(t => t.GoogleSearch != null));
    }

    [Fact]
    public void Pattern2_ChatOptionsMapper_ReadOnlyFunctionDeclarations_ReplacesAndAppendsDeclaration()
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

        ChatOptionsMapper.Apply(request, options, DefaultModel);

        Assert.NotNull(existingTool.FunctionDeclarations);
        Assert.NotSame(readOnlyDecls, existingTool.FunctionDeclarations);
        Assert.Equal(2, existingTool.FunctionDeclarations.Count);
        Assert.Equal("fn_one", existingTool.FunctionDeclarations[0].Name);
        Assert.Equal("fn_two", existingTool.FunctionDeclarations[1].Name);
    }

    [Fact]
    public void Pattern2_ChatOptionsMapper_ArrayFunctionDeclarations_AppendsDeclaration()
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

        ChatOptionsMapper.Apply(request, options, DefaultModel);

        Assert.NotNull(existingTool.FunctionDeclarations);
        Assert.NotSame(declArray, existingTool.FunctionDeclarations);
        Assert.Equal(2, existingTool.FunctionDeclarations.Count);
        Assert.Equal("fn_one", existingTool.FunctionDeclarations[0].Name);
        Assert.Equal("fn_two", existingTool.FunctionDeclarations[1].Name);
    }

    [Fact]
    public void Pattern2_ChatOptionsMapper_OptionsToolsReadOnlyCollection_MapsAllTools()
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
        ChatOptionsMapper.Apply(request, options, DefaultModel);

        Assert.NotNull(request.Tools);
        Assert.Equal(2, request.Tools.Count);
        Assert.Equal("fn_readonly", request.Tools[0].FunctionDeclarations![0].Name);
        Assert.NotNull(request.Tools[1].GoogleSearch);
    }

    [Fact]
    public void Pattern2_ChatOptionsMapper_OptionsToolsArray_MapsAllTools()
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
        ChatOptionsMapper.Apply(request, options, DefaultModel);

        Assert.NotNull(request.Tools);
        Assert.Equal(2, request.Tools.Count);
        Assert.Equal("fn_array", request.Tools[0].FunctionDeclarations![0].Name);
        Assert.NotNull(request.Tools[1].CodeExecution);
    }

    [Fact]
    public void Pattern2_ChatMessageContents_CustomNonListEnumerable_MapsAllParts()
    {
        var customContents = new CustomAIContentCollection(
        [
            new TextContent("Hello custom non-list"),
            new DataContent(new byte[] { 1, 2, 3, 4 }, "image/png"),
            new UriContent(new Uri("https://example.com/audio.mp3"), "audio/mp3")
        ]);

        var message = new ChatMessage(ChatRole.User, customContents);
        var request = ChatRequestMapper.ToRequest([message], options: null, client: null!, DefaultModel, JsonSerializerOptions.Default);

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
    public void Pattern2_ChatMessageContents_CustomReadOnlyCollection_ConsecutiveMessagesMerged()
    {
        var msg1Contents = new CustomReadOnlyAIContentCollection([new TextContent("Message 1 text")]);
        var msg2Contents = new CustomReadOnlyAIContentCollection([new TextContent("Message 2 text")]);

        var msg1 = new ChatMessage(ChatRole.User, msg1Contents);
        var msg2 = new ChatMessage(ChatRole.User, msg2Contents);

        var request = ChatRequestMapper.ToRequest([msg1, msg2], options: null, client: null!, DefaultModel, JsonSerializerOptions.Default);

        // Consecutive User messages should be merged into a single Content object
        Assert.Single(request.Contents);
        var parts = request.Contents[0].Parts;
        Assert.Equal(2, parts.Count);
        Assert.Equal("Message 1 text", parts[0].Text);
        Assert.Equal("Message 2 text", parts[1].Text);
    }

    [Fact]
    public void Pattern2_ChatMessageContents_MergingWithReadOnlyParts_ReplacesWithMutableList()
    {
        var template = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = Role.User,
                    Parts = new ReadOnlyCollection<Part>([Part.FromText("Initial readonly part")])
                }
            ]
        };

        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => template
        };

        var incoming = new ChatMessage(ChatRole.User, "Appended text from user");
        var request = ChatRequestMapper.ToRequest([incoming], options, client: null!, DefaultModel, JsonSerializerOptions.Default);

        Assert.Single(request.Contents);
        var parts = request.Contents[0].Parts;
        Assert.Equal(2, parts.Count);
        Assert.Equal("Initial readonly part", parts[0].Text);
        Assert.Equal("Appended text from user", parts[1].Text);
    }

    [Fact]
    public void Pattern2_ChatRequestMapper_LazyMessagesEnumerable_MapsAllMessages()
    {
        static IEnumerable<ChatMessage> CreateLazyMessages()
        {
            yield return new ChatMessage(ChatRole.User, "User prompt 1");
            yield return new ChatMessage(ChatRole.Assistant, "Model reply 1");
            yield return new ChatMessage(ChatRole.User, "User prompt 2");
        }

        var request = ChatRequestMapper.ToRequest(CreateLazyMessages(), options: null, client: null!, DefaultModel, JsonSerializerOptions.Default);

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
    public void Pattern5_AreUnmodifiedArguments_CaseInsensitiveKeyMismatch_ReturnsFalseAndReSerializesWithUserCasing()
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
            DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);

        // When casing differs, cache MUST NOT be reused; wire must reflect user's "CITY" property
        Assert.True(replayedCall.Args.Value.TryGetProperty("CITY", out var cityProp));
        Assert.Equal("Tokyo", cityProp.GetString());
        Assert.False(replayedCall.Args.Value.TryGetProperty("city", out _));
    }

    [Fact]
    public void Pattern5_AreUnmodifiedArguments_DynamicallyAddedArgument_BypassesCacheAndSerializesAllArguments()
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
            DefaultModel,
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
    public void Pattern5_AreUnmodifiedArguments_DynamicallyAddedJsonElementArgument_BypassesCache()
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
            DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);

        Assert.True(replayedCall.Args.Value.TryGetProperty("limit", out var limitProp));
        Assert.Equal(10, limitProp.GetInt32());
        Assert.True(replayedCall.Args.Value.TryGetProperty("city", out var cityProp));
        Assert.Equal("Tokyo", cityProp.GetString());
    }

    [Fact]
    public void Pattern5_AreUnmodifiedArguments_MutatedJsonElementValue_BypassesCache()
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
            DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);
        Assert.Equal("Kyoto", replayedCall.Args.Value.GetProperty("city").GetString());
    }

    [Fact]
    public void Pattern5_AreUnmodifiedArguments_MutatedClrObjectValue_BypassesCache()
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
            DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);
        Assert.Equal("Osaka", replayedCall.Args.Value.GetProperty("city").GetString());
    }

    [Fact]
    public void Pattern5_AreUnmodifiedArguments_RemovedArgument_BypassesCache()
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
            DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);
        Assert.True(replayedCall.Args.Value.TryGetProperty("city", out var cityProp));
        Assert.Equal("Tokyo", cityProp.GetString());
        Assert.False(replayedCall.Args.Value.TryGetProperty("units", out _));
    }

    [Fact]
    public void Pattern5_AreUnmodifiedArguments_UnmodifiedArguments_ReusesCachedRawArgs()
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
            DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.NotNull(replayedCall.Args);
        Assert.True(JsonElement.DeepEquals(rawArgs, replayedCall.Args.Value));
    }

    [Fact]
    public void Pattern5_AreUnmodifiedArguments_DirectReflectionAudit_ValidatesAllEdgeCases()
    {
        using var wireDoc = JsonDocument.Parse("""{"city":"Tokyo"}""");
        var wireArgs = wireDoc.RootElement.Clone();
        var jsonTokyo = wireArgs.GetProperty("city").Clone();

        using var kyotoDoc = JsonDocument.Parse("\"Kyoto\"");
        var jsonKyoto = kyotoDoc.RootElement.Clone();

        using var arrayDoc = JsonDocument.Parse("[]");
        var jsonArray = arrayDoc.RootElement.Clone();

        using var emptyObjDoc = JsonDocument.Parse("{}");
        var jsonEmptyObj = emptyObjDoc.RootElement.Clone();

        // 1. Exact match -> TRUE
        var exactDict = new Dictionary<string, object?> { ["city"] = jsonTokyo };
        Assert.True(InvokeAreUnmodifiedArguments(exactDict, wireArgs));

        // 2. Case-insensitive key mismatch ("CITY" vs "city") -> FALSE
        var caseMismatchDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["CITY"] = jsonTokyo };
        Assert.False(InvokeAreUnmodifiedArguments(caseMismatchDict, wireArgs));

        // 3. Dynamically added key (2 keys vs 1 property) -> FALSE
        var addedKeyDict = new Dictionary<string, object?> { ["city"] = jsonTokyo, ["extra"] = jsonTokyo };
        Assert.False(InvokeAreUnmodifiedArguments(addedKeyDict, wireArgs));

        // 4. Removed key (0 keys vs 1 property) -> FALSE
        var removedKeyDict = new Dictionary<string, object?>();
        Assert.False(InvokeAreUnmodifiedArguments(removedKeyDict, wireArgs));

        // 5. Non-JsonElement value (plain string "Tokyo") -> FALSE
        var nonJsonElementDict = new Dictionary<string, object?> { ["city"] = "Tokyo" };
        Assert.False(InvokeAreUnmodifiedArguments(nonJsonElementDict, wireArgs));

        // 6. Mutated JsonElement value ("Kyoto" vs "Tokyo") -> FALSE
        var mutatedValDict = new Dictionary<string, object?> { ["city"] = jsonKyoto };
        Assert.False(InvokeAreUnmodifiedArguments(mutatedValDict, wireArgs));

        // 7. Value is null -> FALSE
        var nullValDict = new Dictionary<string, object?> { ["city"] = null };
        Assert.False(InvokeAreUnmodifiedArguments(nullValDict, wireArgs));

        // 8. RawArgs is null -> FALSE
        Assert.False(InvokeAreUnmodifiedArguments(exactDict, null));

        // 9. RawArgs is undefined (default) -> FALSE
        Assert.False(InvokeAreUnmodifiedArguments(exactDict, default(JsonElement)));

        // 10. RawArgs is an Array (not Object) -> FALSE
        Assert.False(InvokeAreUnmodifiedArguments(exactDict, jsonArray));

        // 11. Empty dict vs Empty Object -> TRUE
        Assert.True(InvokeAreUnmodifiedArguments(new Dictionary<string, object?>(), jsonEmptyObj));

        // 12. Empty dict vs 1-prop Object -> FALSE
        Assert.False(InvokeAreUnmodifiedArguments(new Dictionary<string, object?>(), wireArgs));
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
