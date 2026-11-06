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

public sealed class ChatMappingEdgeCaseTests
{
    private const string DefaultModel = "gemini-2.5-flash";

    #region 1. Streaming Grounding Resolution & partIndex == 0

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

    #endregion

    #region 3. Turn Merging Edge Cases

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

    #endregion

    #region 4. Replayed Arguments

    [Fact]
    public void ReplayedArguments_DeeplyNestedObject_ReturnsTrueWhenIdentical_AndReusesRawArgs()
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

        // Unmodified arguments
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
    public void ReplayedArguments_DeeplyNestedObject_MutatedNestedValue_ReserializesUpdatedValue()
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
    public void ReplayedArguments_NullArgumentInDictionary_ReserializesProperly()
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
    public void ReplayedArguments_LiteralJsonNull_PreservesUnmodifiedArgs()
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
    public void ReplayedArguments_ArrayOrderChanged_ReserializesUpdatedOrder()
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
    public void ReplayedArguments_BooleanMutation_ReserializesUpdatedBoolean()
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
