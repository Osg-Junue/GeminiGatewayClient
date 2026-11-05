using System.Collections;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using GeminiGatewayClient;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace GeminiGatewayClient.Tests;

public sealed class AdversarialM1VerificationTests
{
    private const string DefaultModel = "gemini-2.5-flash";

    #region 1. Streaming Grounding Resolution & partIndex == 0

    [Fact]
    public void StreamingGrounding_SinglePartChunk_ExplicitPartIndexZero_ResolvesToSinglePartWithRegion()
    {
        var chunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [Part.FromText("Single part chunk content")]
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com", Title = "Test Source" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 6, Text = "Single" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var stream = new ChatResponseMapper.StreamState();
        var update = ChatResponseMapper.ToChatResponseUpdate(
            chunk,
            DefaultModel,
            stream);

        var textContents = update.Contents.OfType<TextContent>().ToList();
        var tc = Assert.Single(textContents);
        Assert.Equal("Single part chunk content", tc.Text);

        var annotations = tc.Annotations?.OfType<CitationAnnotation>().ToList();
        Assert.NotNull(annotations);
        var cite = Assert.Single(annotations);
        Assert.Equal("Test Source", cite.Title);
        var region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cite.AnnotatedRegions!));
        Assert.Equal(0, region.StartIndex);
        Assert.Equal(6, region.EndIndex);
    }

    [Fact]
    public void StreamingGrounding_MultiPartChunk_ExplicitPartIndexZero_ResolvesExclusivelyToPartZero()
    {
        var chunk = new GenerateContentResponse
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
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/p0", Title = "Source P0" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 5, Text = "Part0" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var stream = new ChatResponseMapper.StreamState();
        var update = ChatResponseMapper.ToChatResponseUpdate(
            chunk,
            DefaultModel,
            stream);

        var textContents = update.Contents.OfType<TextContent>().ToList();
        Assert.Equal(3, textContents.Count);

        // Part 0 must have the citation with exact region
        var p0Cites = textContents[0].Annotations?.OfType<CitationAnnotation>().ToList();
        Assert.NotNull(p0Cites);
        var p0Cite = Assert.Single(p0Cites);
        Assert.Equal("Source P0", p0Cite.Title);
        var p0Region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(p0Cite.AnnotatedRegions!));
        Assert.Equal(0, p0Region.StartIndex);
        Assert.Equal(5, p0Region.EndIndex);

        // Part 1 and Part 2 must NOT have any citations attached
        Assert.Null(textContents[1].Annotations);
        Assert.Null(textContents[2].Annotations);
    }

    [Fact]
    public void StreamingGrounding_MultiPartChunk_ExplicitPartIndexOne_ResolvesExclusivelyToPartOne()
    {
        var chunk = new GenerateContentResponse
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
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/p1", Title = "Source P1" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 1, StartIndex = 0, EndIndex = 5, Text = "Part1" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var stream = new ChatResponseMapper.StreamState();
        var update = ChatResponseMapper.ToChatResponseUpdate(
            chunk,
            DefaultModel,
            stream);

        var textContents = update.Contents.OfType<TextContent>().ToList();
        Assert.Equal(3, textContents.Count);

        Assert.Null(textContents[0].Annotations);

        var p1Cites = textContents[1].Annotations?.OfType<CitationAnnotation>().ToList();
        Assert.NotNull(p1Cites);
        var p1Cite = Assert.Single(p1Cites);
        Assert.Equal("Source P1", p1Cite.Title);
        var p1Region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(p1Cite.AnnotatedRegions!));
        Assert.Equal(0, p1Region.StartIndex);
        Assert.Equal(5, p1Region.EndIndex);

        Assert.Null(textContents[2].Annotations);
    }

    [Fact]
    public void StreamingGrounding_MultiChunkCumulativeStream_WithPartIndexZero_SpansAcrossSegments()
    {
        // Chunk 1: "Hello world" (11 bytes: 0..11)
        var chunk1 = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("Hello world")] }
                }
            ]
        };

        // Chunk 2: " from Gemini" (12 bytes: 11..23)
        // Grounding support specifies PartIndex = 0 and cumulative bytes [6..16) ("world from")
        var chunk2 = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText(" from Gemini")] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/cross", Title = "Cross Source" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 6, EndIndex = 16, Text = "world from" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var stream = new ChatResponseMapper.StreamState();

        var update1 = ChatResponseMapper.ToChatResponseUpdate(chunk1, DefaultModel, stream);
        Assert.Equal(11, stream.Utf8Bytes);

        var update2 = ChatResponseMapper.ToChatResponseUpdate(chunk2, DefaultModel, stream);
        Assert.Equal(23, stream.Utf8Bytes);

        // When aggregated via ToChatResponse(), the cumulative citation correctly attaches to the streamed text segments
        var aggregated = new[] { update1, update2 }.ToChatResponse();
        var texts = aggregated.Messages[0].Contents.OfType<TextContent>().ToList();
        Assert.Equal(2, texts.Count);

        // Segment 0 ("Hello world"): span 6..11 ("world")
        var seg0Cites = texts[0].Annotations?.OfType<CitationAnnotation>().ToList();
        Assert.NotNull(seg0Cites);
        var seg0Cite = Assert.Single(seg0Cites);
        Assert.Equal("Cross Source", seg0Cite.Title);
        var seg0Region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(seg0Cite.AnnotatedRegions!));
        Assert.Equal(6, seg0Region.StartIndex);
        Assert.Equal(11, seg0Region.EndIndex);

        // Segment 1 (" from Gemini"): span 0..5 (" from")
        var seg1Cites = texts[1].Annotations?.OfType<CitationAnnotation>().ToList();
        Assert.NotNull(seg1Cites);
        var seg1Cite = Assert.Single(seg1Cites);
        Assert.Equal("Cross Source", seg1Cite.Title);
        var seg1Region = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(seg1Cite.AnnotatedRegions!));
        Assert.Equal(0, seg1Region.StartIndex);
        Assert.Equal(5, seg1Region.EndIndex);
    }

    [Fact]
    public void StreamingGrounding_MultiPartChunk_NonTextPartAtIndexZero_FallsThroughGracefullyWithoutThrowing()
    {
        var chunk = new GenerateContentResponse
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
                            new Part { FunctionCall = new FunctionCall { Name = "test_tool" } },
                            Part.FromText("Actual text part")
                        ]
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com", Title = "Source" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 4, Text = "test" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var stream = new ChatResponseMapper.StreamState();
        var update = ChatResponseMapper.ToChatResponseUpdate(
            chunk,
            DefaultModel,
            stream);

        // Part 0 is not text; mapping should not crash and should produce FunctionCallContent and TextContent
        Assert.Contains(update.Contents, c => c is FunctionCallContent);
        Assert.Contains(update.Contents, c => c is TextContent);
    }

    [Fact]
    public void GroundingMetadata_WithMoreThan64Chunks_CorrectlyTracksReferencedAndUnreferenced()
    {
        const int totalChunks = 70;
        var chunks = new List<GroundingChunk>(totalChunks);
        for (var i = 0; i < totalChunks; i++)
        {
            chunks.Add(new GroundingChunk
            {
                Web = new GroundingChunkWeb
                {
                    Uri = $"https://example.com/{i}",
                    Title = $"Chunk {i}"
                }
            });
        }

        // Reference chunk 0, 63, 64, and 69
        var referencedIndices = new[] { 0, 63, 64, 69 };
        var supports = new List<GroundingSupport>
        {
            new()
            {
                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 0, EndIndex = 4, Text = "Word" },
                GroundingChunkIndices = referencedIndices
            }
        };

        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("Word text content")] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks = chunks,
                        GroundingSupports = supports
                    }
                }
            ]
        };

        var chatResponse = ChatResponseMapper.ToChatResponse(response, DefaultModel);
        var textContent = Assert.IsType<TextContent>(Assert.Single(chatResponse.Messages[0].Contents));
        Assert.NotNull(textContent.Annotations);

        // The text content should have:
        // 4 referenced chunk annotations (attached directly to target from supports)
        // PLUS (70 - 4 = 66) unreferenced chunk annotations fallback-attached to primaryText!
        // Total = 70 annotations!
        var citations = textContent.Annotations.OfType<CitationAnnotation>().ToList();
        Assert.Equal(70, citations.Count);

        // Verify that chunks 0, 63, 64, 69 have AnnotatedRegions, while unreferenced ones do not
        var withRegions = citations.Where(c => c.AnnotatedRegions is { Count: > 0 }).ToList();
        Assert.Equal(4, withRegions.Count);

        var titlesWithRegions = withRegions.Select(c => c.Title).ToHashSet();
        Assert.Contains("Chunk 0", titlesWithRegions);
        Assert.Contains("Chunk 63", titlesWithRegions);
        Assert.Contains("Chunk 64", titlesWithRegions);
        Assert.Contains("Chunk 69", titlesWithRegions);
    }

    #endregion

    #region 2. Elimination of ResolvedTargets & Boundaries

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(999)]
    public void ResolveTargetsAndRegions_InvalidPartIndex_DoesNotThrow_FallsThroughGracefully(int invalidPartIndex)
    {
        var chunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [Part.FromText("Standard text")]
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com", Title = "Source" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                Segment = new GroundingSegment { PartIndex = invalidPartIndex, StartIndex = 0, EndIndex = 5, Text = "Stand" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var stream = new ChatResponseMapper.StreamState();

        // Must not throw ArgumentOutOfRangeException or IndexOutOfRangeException
        var update = ChatResponseMapper.ToChatResponseUpdate(
            chunk,
            DefaultModel,
            stream);

        Assert.NotNull(update);
    }

    [Fact]
    public void ResolveTargetsAndRegions_InvertedByteIndices_HandledGracefullyWithoutThrowing()
    {
        var chunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [Part.FromText("Valid text content")]
                    },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com", Title = "Source" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                // EndIndex < StartIndex: inverted range
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 10, EndIndex = 5, Text = "Inverted" },
                                GroundingChunkIndices = [0]
                            }
                        ]
                    }
                }
            ]
        };

        var stream = new ChatResponseMapper.StreamState();

        var update = ChatResponseMapper.ToChatResponseUpdate(
            chunk,
            DefaultModel,
            stream);

        var tc = Assert.Single(update.Contents.OfType<TextContent>());
        var cite = Assert.Single(tc.Annotations!.OfType<CitationAnnotation>());
        // Inverted range produces null AnnotatedRegions rather than throwing
        Assert.Null(cite.AnnotatedRegions);
    }

    #endregion

    #region 3. Turn Merging Edge Cases

    [Fact]
    public void TurnMerging_WithReadOnlyCollection_MergesIntoSingleTurn()
    {
        using var geminiClient = new GeminiClient("key", DefaultModel);
        var chatClient = geminiClient.AsIChatClient();

        var roc = new ReadOnlyCollection<Part>(new List<Part> { Part.FromText("ROC part 1") });
        var templateRequest = new GenerateContentRequest
        {
            Contents = [new Content { Role = Role.User, Parts = roc }]
        };

        var options = new ChatOptions { RawRepresentationFactory = _ => templateRequest };
        ChatMessage[] messages =
        [
            new(ChatRole.User, "Appended user message 2"),
            new(ChatRole.User, "Appended user message 3")
        ];

        var request = ChatRequestMapper.ToRequest(
            messages,
            options,
            chatClient,
            DefaultModel,
            JsonSerializerOptions.Default);

        Assert.Single(request.Contents);
        Assert.Equal(Role.User, request.Contents[0].Role);
        Assert.Equal(3, request.Contents[0].Parts.Count);
        Assert.Equal("ROC part 1", request.Contents[0].Parts[0].Text);
        Assert.Equal("Appended user message 2", request.Contents[0].Parts[1].Text);
        Assert.Equal("Appended user message 3", request.Contents[0].Parts[2].Text);
    }

    [Fact]
    public void TurnMerging_WithCustomReadOnlyIList_MergesIntoSingleTurn()
    {
        using var geminiClient = new GeminiClient("key", DefaultModel);
        var chatClient = geminiClient.AsIChatClient();

        var customList = new CustomReadOnlyPartList([Part.FromText("Custom list part 1")]);
        var templateRequest = new GenerateContentRequest
        {
            Contents = [new Content { Role = Role.User, Parts = customList }]
        };

        var options = new ChatOptions { RawRepresentationFactory = _ => templateRequest };
        ChatMessage[] messages =
        [
            new(ChatRole.User, "Appended user message 2")
        ];

        var request = ChatRequestMapper.ToRequest(
            messages,
            options,
            chatClient,
            DefaultModel,
            JsonSerializerOptions.Default);

        Assert.Single(request.Contents);
        Assert.Equal(Role.User, request.Contents[0].Role);
        Assert.Equal(2, request.Contents[0].Parts.Count);
        Assert.Equal("Custom list part 1", request.Contents[0].Parts[0].Text);
        Assert.Equal("Appended user message 2", request.Contents[0].Parts[1].Text);
    }

    [Fact]
    public void TurnMerging_WithZeroPartAndEmptyMessages_PreservesAlternationWithoutDuplicates()
    {
        using var geminiClient = new GeminiClient("key", DefaultModel);
        var chatClient = geminiClient.AsIChatClient();

        ChatMessage[] messages =
        [
            new(ChatRole.User, "Hello"),
            new(ChatRole.User, []), // empty contents
            new(ChatRole.User, [new TextContent("")]), // empty string content
            new(ChatRole.User, "World"),
            new(ChatRole.Assistant, []), // empty assistant message
            new(ChatRole.Assistant, "Response"),
            new(ChatRole.User, "Follow-up")
        ];

        var request = ChatRequestMapper.ToRequest(
            messages,
            options: null,
            chatClient,
            DefaultModel,
            JsonSerializerOptions.Default);

        // Turn 0: User ("Hello", "World")
        // Turn 1: Model ("Response")
        // Turn 2: User ("Follow-up")
        Assert.Equal(3, request.Contents.Count);

        Assert.Equal(Role.User, request.Contents[0].Role);
        Assert.Equal(2, request.Contents[0].Parts.Count);
        Assert.Equal("Hello", request.Contents[0].Parts[0].Text);
        Assert.Equal("World", request.Contents[0].Parts[1].Text);

        Assert.Equal(Role.Model, request.Contents[1].Role);
        Assert.Single(request.Contents[1].Parts);
        Assert.Equal("Response", request.Contents[1].Parts[0].Text);

        Assert.Equal(Role.User, request.Contents[2].Role);
        Assert.Single(request.Contents[2].Parts);
        Assert.Equal("Follow-up", request.Contents[2].Parts[0].Text);
    }

    [Fact]
    public void TurnMerging_TemplateWithEmptyParts_MergesFirstIncomingMessage()
    {
        using var geminiClient = new GeminiClient("key", DefaultModel);
        var chatClient = geminiClient.AsIChatClient();

        var templateRequest = new GenerateContentRequest
        {
            Contents = [new Content { Role = Role.User, Parts = [] }]
        };

        var options = new ChatOptions { RawRepresentationFactory = _ => templateRequest };
        ChatMessage[] messages = [new(ChatRole.User, "First real message")];

        var request = ChatRequestMapper.ToRequest(
            messages,
            options,
            chatClient,
            DefaultModel,
            JsonSerializerOptions.Default);

        Assert.Single(request.Contents);
        Assert.Equal(Role.User, request.Contents[0].Role);
        Assert.Single(request.Contents[0].Parts);
        Assert.Equal("First real message", request.Contents[0].Parts[0].Text);
    }

    #endregion

    #region 4. AreUnmodifiedArguments Deep Verification

    [Fact]
    public void AreUnmodifiedArguments_DeeplyNestedObject_ReturnsTrueWhenIdentical_AndReusesRawArgs()
    {
        const string json = """
            {
              "config": {
                "db": {
                  "host": "localhost",
                  "ports": [5432, 5433]
                },
                "flags": {
                  "enabled": true
                }
              },
              "tags": ["prod", "primary"]
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var rawArgs = doc.RootElement.Clone();

        var modelResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [new Part { FunctionCall = new FunctionCall { Name = "setup", Args = rawArgs } }]
                    }
                }
            ]
        };

        var assistantMsg = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel).Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Unmodified arguments: reuses rawCall.Args
        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.True(JsonElement.DeepEquals(rawArgs, replayedCall.Args!.Value));
    }

    [Fact]
    public void AreUnmodifiedArguments_DeeplyNestedObject_MutatedNestedValue_ReserializesUpdatedValue()
    {
        const string json = """
            {
              "config": {
                "db": {
                  "host": "localhost",
                  "port": 5432
                }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var rawArgs = doc.RootElement.Clone();

        var modelResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [new Part { FunctionCall = new FunctionCall { Name = "setup", Args = rawArgs } }]
                    }
                }
            ]
        };

        var assistantMsg = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel).Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Mutate deep value inside config
        fnCall.Arguments!["config"] = new Dictionary<string, object?>
        {
            ["db"] = new Dictionary<string, object?>
            {
                ["host"] = "localhost",
                ["port"] = 9999
            }
        };

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        var replayedPort = replayedCall.Args!.Value
            .GetProperty("config")
            .GetProperty("db")
            .GetProperty("port")
            .GetInt32();

        Assert.Equal(9999, replayedPort);
    }

    [Fact]
    public void AreUnmodifiedArguments_NullArgumentInDictionary_ReserializesProperly()
    {
        const string json = """{"city":"Tokyo"}""";
        using var doc = JsonDocument.Parse(json);
        var rawArgs = doc.RootElement.Clone();

        var modelResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [new Part { FunctionCall = new FunctionCall { Name = "forecast", Args = rawArgs } }]
                    }
                }
            ]
        };

        var assistantMsg = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel).Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Set value to C# null
        fnCall.Arguments!["city"] = null;

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.Equal(JsonValueKind.Null, replayedCall.Args!.Value.GetProperty("city").ValueKind);
    }

    [Fact]
    public void AreUnmodifiedArguments_LiteralJsonNull_PreservesUnmodifiedArgs()
    {
        const string json = """{"city":null}""";
        using var doc = JsonDocument.Parse(json);
        var rawArgs = doc.RootElement.Clone();

        var modelResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [new Part { FunctionCall = new FunctionCall { Name = "forecast", Args = rawArgs } }]
                    }
                }
            ]
        };

        var assistantMsg = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel).Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Dictionary contains JsonElement with ValueKind.Null unmodified
        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.True(JsonElement.DeepEquals(rawArgs, replayedCall.Args!.Value));
    }

    [Fact]
    public void AreUnmodifiedArguments_ArrayOrderChanged_ReserializesUpdatedOrder()
    {
        const string json = """{"numbers":[1,2,3]}""";
        using var doc = JsonDocument.Parse(json);
        var rawArgs = doc.RootElement.Clone();

        var modelResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [new Part { FunctionCall = new FunctionCall { Name = "calculate", Args = rawArgs } }]
                    }
                }
            ]
        };

        var assistantMsg = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel).Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        using var modifiedDoc = JsonDocument.Parse("[3,2,1]");
        fnCall.Arguments!["numbers"] = modifiedDoc.RootElement.Clone();

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        var firstNum = replayedCall.Args!.Value.GetProperty("numbers")[0].GetInt32();
        Assert.Equal(3, firstNum);
    }

    [Fact]
    public void StreamingGrounding_TrailingChunkWithNoText_AttachesCitationsToEarlierChunkAndEmitsCarrier()
    {
        // Chunk 1: "First chunk text" (16 bytes)
        var chunk1 = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("First chunk text")] }
                }
            ]
        };

        // Chunk 2: empty text (""), but has grounding metadata referencing chunk 1 (span 0..5 = "First")
        var chunk2 = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("")] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/trailing", Title = "Trailing Source" } }
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

        var stream = new ChatResponseMapper.StreamState();

        var update1 = ChatResponseMapper.ToChatResponseUpdate(chunk1, DefaultModel, stream);
        Assert.Single(update1.Contents.OfType<TextContent>());

        var update2 = ChatResponseMapper.ToChatResponseUpdate(chunk2, DefaultModel, stream);
        // In chunk 2, because there is no TextContent, a carrier AIContent is emitted
        var carrier = Assert.Single(update2.Contents);
        Assert.IsNotType<TextContent>(carrier);
        Assert.NotNull(carrier.Annotations);
        Assert.Single(carrier.Annotations);

        // When aggregated via ToChatResponse, chunk 1's TextContent has the citation attached with region 0..5!
        var aggregated = new[] { update1, update2 }.ToChatResponse();
        var tc = Assert.Single(aggregated.Messages[0].Contents.OfType<TextContent>());
        Assert.Equal("First chunk text", tc.Text);
        var cite = Assert.Single(tc.Annotations!.OfType<CitationAnnotation>());
        Assert.Equal("Trailing Source", cite.Title);
        var reg = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cite.AnnotatedRegions!));
        Assert.Equal(0, reg.StartIndex);
        Assert.Equal(5, reg.EndIndex);
    }

    [Fact]
    public void StreamingGrounding_NullStartByte_DefaultsToZero_AndNullEndByte_DefaultsToTextLength()
    {
        var chunk = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Index = 0,
                    Content = new Content { Role = Role.Model, Parts = [Part.FromText("Sample content")] },
                    GroundingMetadata = new GroundingMetadata
                    {
                        GroundingChunks =
                        [
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/1", Title = "Source 1" } },
                            new GroundingChunk { Web = new GroundingChunkWeb { Uri = "https://example.com/2", Title = "Source 2" } }
                        ],
                        GroundingSupports =
                        [
                            new GroundingSupport
                            {
                                // StartIndex null -> 0, EndIndex 6
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = null, EndIndex = 6, Text = "Sample" },
                                GroundingChunkIndices = [0]
                            },
                            new GroundingSupport
                            {
                                // StartIndex 7, EndIndex null -> length (14)
                                Segment = new GroundingSegment { PartIndex = 0, StartIndex = 7, EndIndex = null, Text = "content" },
                                GroundingChunkIndices = [1]
                            }
                        ]
                    }
                }
            ]
        };

        var response = ChatResponseMapper.ToChatResponse(chunk, DefaultModel);
        var tc = Assert.Single(response.Messages[0].Contents.OfType<TextContent>());
        var cites = tc.Annotations!.OfType<CitationAnnotation>().ToList();
        Assert.Equal(2, cites.Count);

        var reg1 = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cites[0].AnnotatedRegions!));
        Assert.Equal(0, reg1.StartIndex);
        Assert.Equal(6, reg1.EndIndex);

        var reg2 = Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(cites[1].AnnotatedRegions!));
        Assert.Equal(7, reg2.StartIndex);
        Assert.Equal(14, reg2.EndIndex);
    }

    [Fact]
    public void TurnMerging_ContentWithNullParts_SafeDefaultsToEmptyListWithoutAllocatingOrThrowing()
    {
        var content = new Content();
        // Setting Parts to null must fall back to empty list rather than leaving _parts null
        content.Parts = null!;
        Assert.NotNull(content.Parts);
        Assert.Empty(content.Parts);

        var cloned = content.Clone();
        Assert.NotNull(cloned.Parts);
        Assert.Empty(cloned.Parts);
    }

    [Fact]
    public void AreUnmodifiedArguments_BooleanMutation_ReserializesUpdatedBoolean()
    {
        const string json = """{"enabled":true,"count":10}""";
        using var doc = JsonDocument.Parse(json);
        var rawArgs = doc.RootElement.Clone();

        var modelResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [new Part { FunctionCall = new FunctionCall { Name = "toggle", Args = rawArgs } }]
                    }
                }
            ]
        };

        var assistantMsg = ChatResponseMapper.ToChatResponse(modelResponse, DefaultModel).Messages[0];
        var fnCall = Assert.Single(assistantMsg.Contents.OfType<FunctionCallContent>());

        // Mutate bool from true to false
        fnCall.Arguments!["enabled"] = false;

        var request = ChatRequestMapper.ToRequest(
            [assistantMsg],
            options: null,
            client: null!,
            modelId: DefaultModel,
            JsonSerializerOptions.Default);

        var replayedCall = request.Contents[0].Parts[0].FunctionCall!;
        Assert.False(replayedCall.Args!.Value.GetProperty("enabled").GetBoolean());
        Assert.Equal(10, replayedCall.Args!.Value.GetProperty("count").GetInt32());
    }

    #endregion

    #region Helper Types

    private sealed class CustomReadOnlyPartList(Part[] items) : IList<Part>
    {
        public Part this[int index] { get => items[index]; set => throw new NotSupportedException(); }
        public int Count => items.Length;
        public bool IsReadOnly => true;
        public void Add(Part item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(Part item) => items.Contains(item);
        public void CopyTo(Part[] array, int arrayIndex) => items.CopyTo(array, arrayIndex);
        public IEnumerator<Part> GetEnumerator() => ((IEnumerable<Part>)items).GetEnumerator();
        public int IndexOf(Part item) => Array.IndexOf(items, item);
        public void Insert(int index, Part item) => throw new NotSupportedException();
        public bool Remove(Part item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
    }

    #endregion
}
