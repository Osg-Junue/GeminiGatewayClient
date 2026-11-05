using System.Collections;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Models;
using GeminiGatewayClient.Serialization;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// Adversarial regression tests for the validation, tool-value, and buffered-read simplifications, plus grounding offsets:
/// each one pins behavior that an allocation-driven rewrite could plausibly have changed.
/// </summary>
public sealed class AdversarialValidationAndToolConversionTests
{
    private const string Model = "gemini-2.5-flash";

    private static JsonElement Json(string json) => JsonElement.Parse(json);

    private static string Raw(JsonElement element) => element.GetRawText();

    #region Validation: messages are byte-for-byte stable at every location (pattern 1: index 0 and index > 0)

    public static TheoryData<string, Func<Part>> InvalidParts() => new()
    {
        { " must have at least one field set.", () => new Part() },
        {
            " cannot set more than one primary data field (Text, InlineData, FileData, FunctionCall, FunctionResponse, ExecutableCode, CodeExecutionResult) on the same Part.",
            () => new Part { Text = "x", FileData = new FileData { FileUri = "gs://b/o" } }
        },
        { ".InlineData.MimeType cannot be null or whitespace.", () => new Part { InlineData = new Blob { MimeType = " ", Data = [1] } } },
        { ".InlineData.Data cannot be null or empty.", () => new Part { InlineData = new Blob { MimeType = "image/png", Data = [] } } },
        { ".FileData.FileUri cannot be null or whitespace.", () => new Part { FileData = new FileData { FileUri = " " } } },
        { ".FunctionCall.Name cannot be null or whitespace.", () => new Part { FunctionCall = new FunctionCall { Name = " " } } },
        { ".FunctionCall.Args cannot be JsonValueKind.Undefined.", () => new Part { FunctionCall = new FunctionCall { Name = "f", Args = default(JsonElement) } } },
        { ".FunctionResponse.Name cannot be null or whitespace.", () => new Part { FunctionResponse = new FunctionResponse { Name = " ", Response = Json("{}") } } },
        {
            ".FunctionResponse.Response must be a non-null JSON object (JsonValueKind.Object).",
            () => new Part { FunctionResponse = new FunctionResponse { Name = "f", Response = Json("[]") } }
        }
    };

    [Theory]
    [MemberData(nameof(InvalidParts))]
    public void Validate_InvalidPart_ReportsExactPathAtFirstAndLaterPositions(string reason, Func<Part> createInvalidPart)
    {
        var first = new GenerateContentRequest { Contents = [new Content { Role = Role.User, Parts = [createInvalidPart()] }] };
        var firstError = Assert.Throws<ArgumentException>(() => first.Validate());
        Assert.Equal($"GenerateContentRequest.Contents[0].Parts[0]{reason} (Parameter 'request')", firstError.Message);

        var later = new GenerateContentRequest
        {
            Contents =
            [
                Content.FromText("ok"),
                new Content { Role = Role.Model, Parts = [Part.FromText("ok"), Part.FromText("ok"), createInvalidPart()] }
            ]
        };
        var laterError = Assert.Throws<ArgumentException>(() => later.Validate("custom"));
        Assert.Equal($"GenerateContentRequest.Contents[1].Parts[2]{reason} (Parameter 'custom')", laterError.Message);

        var system = new GenerateContentRequest
        {
            Contents = [Content.FromText("ok")],
            SystemInstruction = new Content { Parts = [Part.FromText("ok"), createInvalidPart()] }
        };
        var systemError = Assert.Throws<ArgumentException>(() => system.Validate());
        Assert.Equal($"GenerateContentRequest.SystemInstruction.Parts[1]{reason} (Parameter 'request')", systemError.Message);

        var embed = new EmbedContentRequest { Content = new Content { Parts = [createInvalidPart()] } };
        var embedError = Assert.Throws<ArgumentException>(() => embed.Validate());
        Assert.Equal($"EmbedContentRequest.Content.Parts[0]{reason} (Parameter 'request')", embedError.Message);

        var batch = new BatchEmbedContentsRequest
        {
            Requests =
            [
                EmbedContentRequest.FromText("ok"),
                new EmbedContentRequest { Content = new Content { Parts = [Part.FromText("ok"), createInvalidPart()] } }
            ]
        };
        var batchError = Assert.Throws<ArgumentException>(() => batch.Validate());
        Assert.Equal($"BatchEmbedContentsRequest.Requests[1].Content.Parts[1]{reason} (Parameter 'request')", batchError.Message);
    }

    [Theory]
    [InlineData(Role.Unknown)]
    [InlineData((Role)99)]
    public void Validate_InvalidRole_ReportsExactPath(Role role)
    {
        var request = new GenerateContentRequest { Contents = [new Content { Role = role, Parts = [Part.FromText("x")] }] };
        Assert.Equal(
            "GenerateContentRequest.Contents[0].Role cannot be Role.Unknown. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => request.Validate()).Message);

        var embed = new EmbedContentRequest { Content = new Content { Role = role, Parts = [Part.FromText("x")] } };
        Assert.Equal(
            "EmbedContentRequest.Content.Role cannot be Role.Unknown. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => embed.Validate()).Message);
    }

    [Fact]
    public void Validate_EmptyParts_RejectedUnlessExtensionDataCarriesTheContent()
    {
        var empty = new GenerateContentRequest { Contents = [new Content { Role = Role.User, Parts = null! }] };
        Assert.Equal(
            "GenerateContentRequest.Contents[0].Parts must contain at least one Part item. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => empty.Validate()).Message);

        var embedEmpty = new EmbedContentRequest { Content = new Content() };
        Assert.Equal(
            "EmbedContentRequest.Content.Parts must contain at least one Part item. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => embedEmpty.Validate()).Message);

        var extensionOnly = new GenerateContentRequest
        {
            Contents = [new Content { ExtensionData = new Dictionary<string, JsonElement> { ["custom"] = Json("1") } }]
        };
        extensionOnly.Validate();

        var emptyExtension = new GenerateContentRequest
        {
            Contents = [new Content { ExtensionData = new Dictionary<string, JsonElement>() }]
        };
        Assert.Throws<ArgumentException>(() => emptyExtension.Validate());
    }

    [Fact]
    public void Validate_NullElementsInAbstractCollections_ReportExactPath()
    {
        IList<Part>[] partCollections =
        [
            new Part[] { Part.FromText("ok"), null! },
            new ReadOnlyCollection<Part>([Part.FromText("ok"), null!]),
            new Collection<Part> { Part.FromText("ok"), null! }
        ];

        foreach (var parts in partCollections)
        {
            var request = new GenerateContentRequest { Contents = [new Content { Role = Role.User, Parts = parts }] };
            Assert.Equal(
                "GenerateContentRequest.Contents[0].Parts[1] cannot be null. (Parameter 'request')",
                Assert.Throws<ArgumentException>(() => request.Validate()).Message);
        }

        var embed = new BatchEmbedContentsRequest
        {
            Requests = [new EmbedContentRequest { Content = new Content { Parts = new[] { (Part)null! } } }]
        };
        Assert.Equal(
            "BatchEmbedContentsRequest.Requests[0].Content.Parts[0] cannot be null. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => embed.Validate()).Message);
    }

    [Fact]
    public void Validate_EmbedRequestRules_ReportExactPathAndKeepExceptionTypes()
    {
        var nullContent = new EmbedContentRequest { Content = null! };
        Assert.Equal("EmbedContentRequest.Content cannot be null. (Parameter 'request')", Assert.Throws<ArgumentException>(() => nullContent.Validate()).Message);

        var unknownTask = EmbedContentRequest.FromText("x", TaskType.Unknown);
        Assert.Equal("EmbedContentRequest.TaskType cannot be TaskType.Unknown. (Parameter 'request')", Assert.Throws<ArgumentException>(() => unknownTask.Validate()).Message);

        var titledQuery = EmbedContentRequest.FromText("x", TaskType.RetrievalQuery, "title");
        Assert.Equal(
            "EmbedContentRequest.Title can only be specified when TaskType is TaskType.RetrievalDocument. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => titledQuery.Validate()).Message);

        foreach (var dimensions in new[] { 0, -1 })
        {
            var batch = new BatchEmbedContentsRequest
            {
                Requests = [EmbedContentRequest.FromText("ok"), EmbedContentRequest.FromText("x", outputDimensionality: dimensions)]
            };
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => batch.Validate());
            Assert.Equal("request", error.ParamName);
            Assert.Equal(dimensions, error.ActualValue);
            Assert.StartsWith("BatchEmbedContentsRequest.Requests[1].OutputDimensionality must be greater than zero.", error.Message, StringComparison.Ordinal);
        }

        EmbedContentRequest.FromText("x", TaskType.RetrievalDocument, "title", 8).Validate();
    }

    [Fact]
    public void Validate_ReportsTheFirstViolationInDocumentOrder()
    {
        var request = new GenerateContentRequest
        {
            Contents =
            [
                new Content { Role = Role.Unknown, Parts = [new Part()] },
                new Content { Role = Role.User, Parts = [new Part()] }
            ],
            SystemInstruction = new Content { Parts = [new Part()] }
        };

        Assert.StartsWith(
            "GenerateContentRequest.Contents[0].Role ",
            Assert.Throws<ArgumentException>(() => request.Validate()).Message,
            StringComparison.Ordinal);
    }

    #endregion

    #region Tool results: every result shape keeps its wire form after the single-writer rewrite

    public sealed record Report(string City, int Temperature);

    public enum Outcome
    {
        Passed = 1
    }

    public static TheoryData<object?, string> ResultShapes() => new()
    {
        { null, """{"result":null}""" },
        { "", """{"result":""}""" },
        { 42, """{"result":42}""" },
        { true, """{"result":true}""" },
        { Json("\"s\""), """{"result":"s"}""" },
        { Json("12.50"), """{"result":12.50}""" },
        { Json("null"), """{"result":null}""" },
        { Json("[1,2]"), """{"result":[1,2]}""" },
        { Json("""{"a":1}"""), """{"a":1}""" },
        { JsonNode.Parse("""{"a":1}""")!, """{"a":1}""" },
        { new JsonObject { ["a"] = null }, """{"a":null}""" },
        { JsonNode.Parse("[1]")!, """{"result":[1]}""" },
        { JsonValue.Create(5), """{"result":5}""" },
        { new Dictionary<string, object?> { ["a"] = 1, ["b"] = null }, """{"a":1,"b":null}""" },
        { new Dictionary<string, object?>(), "{}" },
        { new Dictionary<string, int> { ["a"] = 1 }, """{"a":1}""" },
        { new Dictionary<int, string> { [7] = "x" }, """{"7":"x"}""" },
        { new Hashtable { ["a"] = 1 }, """{"a":1}""" },
        { new List<KeyValuePair<string, int>> { new("a", 1), new("a", 2) }, """{"a":1,"a":2}""" },
        { new Dictionary<string, Report> { ["r"] = new("Tokyo", 18) }, """{"r":{"city":"Tokyo","temperature":18}}""" },
        { new Report("Tokyo", 18), """{"city":"Tokyo","temperature":18}""" },
        { new List<int> { 1, 2 }, """{"result":[1,2]}""" },
        { new[] { "a" }, """{"result":["a"]}""" },
        { Outcome.Passed, """{"result":"Passed"}""" }
    };

    [Theory]
    [MemberData(nameof(ResultShapes))]
    public void ToResponseObject_KeepsWireFormForEveryResultShape(object? result, string expected) =>
        Assert.Equal(expected, JsonSerializer.Serialize(ToolJsonConverter.ToResponseObject(result, AIJsonUtilities.DefaultOptions), GeminiJsonContext.Default.JsonElement));

    [Fact]
    public void ToResponseObject_MultiByteText_RoundTrips()
    {
        var converted = ToolJsonConverter.ToResponseObject("日本語🌸", AIJsonUtilities.DefaultOptions);

        Assert.Equal("日本語🌸", converted.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(70_000)]
    [InlineData(1_000_000)]
    public void ToResponseObject_ResultsLargerThanTheInitialBuffer_AreNotTruncated(int length)
    {
        var text = new string('あ', length);

        var wrapped = ToolJsonConverter.ToResponseObject(text, AIJsonUtilities.DefaultOptions);
        Assert.Equal(text, wrapped.GetProperty("result").GetString());

        var dictionary = ToolJsonConverter.ToResponseObject(new Dictionary<string, object?> { ["text"] = text }, AIJsonUtilities.DefaultOptions);
        Assert.Equal(text, dictionary.GetProperty("text").GetString());

        var node = ToolJsonConverter.ToResponseObject(new JsonObject { ["text"] = text }, AIJsonUtilities.DefaultOptions);
        Assert.Equal(text, node.GetProperty("text").GetString());
    }

    [Fact]
    public void ToResponseObject_ResultOutlivesTheDocumentItCameFrom()
    {
        JsonElement converted;
        using (var document = JsonDocument.Parse("""{"a":[1,2,3]}"""))
        {
            converted = ToolJsonConverter.ToResponseObject(document.RootElement, AIJsonUtilities.DefaultOptions);
        }

        Assert.Equal("""{"a":[1,2,3]}""", Raw(converted));
    }

    [Fact]
    public void ToResponseObject_UnwritableValues_StillFailLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => ToolJsonConverter.ToResponseObject(default(JsonElement), AIJsonUtilities.DefaultOptions));
        Assert.Throws<InvalidOperationException>(() => ToolJsonConverter.ToResponseObject(
            new Dictionary<string, object?> { ["bad"] = default(JsonElement) },
            AIJsonUtilities.DefaultOptions));

        var noMetadata = new JsonSerializerOptions { TypeInfoResolver = GeminiJsonContext.Default };
        Assert.Throws<InvalidOperationException>(() => ToolJsonConverter.ToResponseObject(new Report("Tokyo", 18), noMetadata));
    }

    [Fact]
    public void ToResponseObject_SequenceThatThrowsMidway_PropagatesTheFailure()
    {
        static IEnumerable<KeyValuePair<string, int>> Failing()
        {
            yield return new KeyValuePair<string, int>("a", 1);
            throw new FormatException("boom");
        }

        Assert.Throws<FormatException>(() => ToolJsonConverter.ToResponseObject(Failing(), AIJsonUtilities.DefaultOptions));
    }

    #endregion

    #region Function-call arguments: property counts and raw reuse (pattern 5)

    [Theory]
    [InlineData("{}", 0)]
    [InlineData("""{"a":1}""", 1)]
    [InlineData("""{"a":{"b":1,"c":{"d":2}},"e":[1,2,3],"f":null}""", 3)]
    public void ToDictionary_CountsTopLevelPropertiesOnly(string json, int expectedCount)
    {
        var dictionary = ToolJsonConverter.ToDictionary(Json(json));

        Assert.NotNull(dictionary);
        Assert.Equal(expectedCount, dictionary.Count);
    }

    [Fact]
    public void ToDictionary_DuplicateWireKeys_LastValueWins()
    {
        var dictionary = ToolJsonConverter.ToDictionary(Json("""{"a":1,"a":2}"""));

        var value = Assert.IsType<JsonElement>(Assert.Single(dictionary!).Value);
        Assert.Equal(2, value.GetInt32());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    public void ToDictionary_NonObjectArguments_ReturnNull(string json) =>
        Assert.Null(ToolJsonConverter.ToDictionary(Json(json)));

    [Fact]
    public void ToDictionary_UndefinedArguments_ReturnNull()
    {
        Assert.Null(ToolJsonConverter.ToDictionary(default(JsonElement)));
        Assert.Null(ToolJsonConverter.ToDictionary(null));
    }

    private static ChatMessage ReplayedCall(string argsJson)
    {
        var json = $$$"""{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"id":"c1","name":"f","args":{{{argsJson}}}}}]}}]}""";
        var response = JsonSerializer.Deserialize(json, GeminiJsonContext.Default.GenerateContentResponse)!;
        return ChatResponseMapper.ToChatResponse(response, Model).Messages[0];
    }

    private static string WireArgs(ChatMessage message)
    {
        using var client = new GeminiClient("key", Model);
        var request = ChatRequestMapper.ToRequest([message], null, client.AsIChatClient(), Model, AIJsonUtilities.DefaultOptions);
        return Raw(request.Contents[0].Parts[0].FunctionCall!.Args!.Value);
    }

    private static IDictionary<string, object?> Arguments(ChatMessage message) =>
        Assert.IsType<FunctionCallContent>(Assert.Single(message.Contents)).Arguments!;

    [Fact]
    public void ArgumentReuse_UnchangedArguments_PreserveTheExactWireForm()
    {
        const string args = """{"n":1.50,"nested":{"x":[1,{"y":null}]},"s":"あ"}""";

        Assert.Equal(args, WireArgs(ReplayedCall(args)));
    }

    [Fact]
    public void ArgumentReuse_SameCountDifferentKey_RegeneratesArguments()
    {
        var message = ReplayedCall("""{"a":1,"b":2}""");
        var arguments = Arguments(message);
        arguments.Remove("b");
        arguments["c"] = Json("2");

        Assert.Equal("""{"a":1,"c":2}""", WireArgs(message));
    }

    [Fact]
    public void ArgumentReuse_ClearedArguments_RegeneratesAnEmptyObject()
    {
        var message = ReplayedCall("""{"a":1}""");
        Arguments(message).Clear();

        Assert.Equal("{}", WireArgs(message));
    }

    [Fact]
    public void ArgumentReuse_EmptyRawObject_CallerAddsKey_RegeneratesArguments()
    {
        var message = ReplayedCall("{}");
        Arguments(message)["a"] = 1;

        Assert.Equal("""{"a":1}""", WireArgs(message));
    }

    [Fact]
    public void ArgumentReuse_DuplicateWireKeys_RegeneratesTheCollapsedForm()
    {
        Assert.Equal("""{"a":2}""", WireArgs(ReplayedCall("""{"a":1,"a":2}""")));
    }

    [Fact]
    public void ArgumentReuse_NullValueReplacingJsonElement_RegeneratesArguments()
    {
        var message = ReplayedCall("""{"a":1}""");
        Arguments(message)["a"] = null;

        Assert.Equal("""{"a":null}""", WireArgs(message));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[1]")]
    public void ArgumentReuse_NonObjectRawArgs_AreNeverReused(string rawArgs)
    {
        var rawPart = new Part { FunctionCall = new FunctionCall { Id = "c1", Name = "f", Args = Json(rawArgs) } };
        var message = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("c1", "f", new Dictionary<string, object?>()) { RawRepresentation = rawPart }]);

        Assert.Equal("{}", WireArgs(message));
    }

    #endregion

    #region Grounding offsets: differential check of the byte-to-char conversion against an independent oracle

    /// <summary>
    /// Independent oracle: walks runes with their own UTF-8/UTF-16 lengths instead of the mapper's decoder.
    /// </summary>
    private static (int Start, int End)? ReferenceRegion(string text, int start, int? end)
    {
        var boundaries = new List<(int Bytes, int Chars)> { (0, 0) };
        foreach (var rune in text.EnumerateRunes())
        {
            var (bytes, chars) = boundaries[^1];
            boundaries.Add((bytes + rune.Utf8SequenceLength, chars + rune.Utf16SequenceLength));
        }

        if (start > boundaries[^1].Bytes)
        {
            return null;
        }

        var charStart = boundaries.First(b => b.Bytes >= start).Chars;
        var charEnd = end is { } e && e < boundaries[^1].Bytes ? boundaries.First(b => b.Bytes >= e).Chars : text.Length;
        return (charStart, charEnd);
    }

    private static (int Start, int End)? MappedRegion(string text, int start, int? end)
    {
        var response = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content { Role = Role.Model, Parts = [new Part { Text = text }] },
                    CitationMetadata = new CitationMetadata { Citations = [new Citation { StartIndex = start, EndIndex = end, Uri = "https://example.com" }] }
                }
            ]
        };

        var content = Assert.IsType<TextContent>(Assert.Single(ChatResponseMapper.ToChatResponse(response, Model).Messages[0].Contents));
        var citation = Assert.IsType<CitationAnnotation>(Assert.Single(content.Annotations!));
        return citation.AnnotatedRegions is [TextSpanAnnotatedRegion region]
            ? (region.StartIndex!.Value, region.EndIndex!.Value)
            : null;
    }

    [Fact]
    public void Offsets_MatchAnIndependentOracle_ForRandomMixedTextIncludingLoneSurrogates()
    {
        string[] alphabet = ["a", "é", "あ", "🌸", "𠮷", "\uD83C", "\uDF38", "�", "\u0000"];
        var random = new Random(20261105);

        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var builder = new StringBuilder("あ");
            for (var i = random.Next(0, 24); i > 0; i--)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var text = builder.ToString();
            var utf8Length = Encoding.UTF8.GetByteCount(text);
            var start = random.Next(0, utf8Length + 3);
            int? end = random.Next(4) == 0 ? null : start + random.Next(0, utf8Length + 3);

            var expected = ReferenceRegion(text, start, end);
            var actual = MappedRegion(text, start, end);
            Assert.True(expected == actual, $"text={Convert.ToHexString(Encoding.Unicode.GetBytes(text))} start={start} end={end} expected={expected} actual={actual}");
        }
    }

    [Theory]
    [InlineData("あ", 0, 3, 0, 1)]
    [InlineData("あ", 1, 2, 1, 1)]
    [InlineData("あい", 3, 6, 1, 2)]
    [InlineData("🌸a", 0, 4, 0, 2)]
    [InlineData("🌸a", 4, 5, 2, 3)]
    [InlineData("🌸a", 2, 5, 2, 3)]
    [InlineData("\uD83Ca", 3, 4, 1, 2)]
    [InlineData("a\uD83C", 1, 4, 1, 2)]
    public void Offsets_AlignToCharacterBoundaries(string text, int start, int end, int expectedStart, int expectedEnd) =>
        Assert.Equal((expectedStart, expectedEnd), MappedRegion(text, start, end));

    #endregion

    #region Buffered responses: known-length sizing and limits (pattern 4)

    private const string BodyPrefix = """{"candidates":[{"content":{"role":"model","parts":[{"text":" """;
    private const string BodySuffix = """ "}]}}]}""";

    private static string BodyOfExactLength(int length) =>
        BodyPrefix + new string('x', length - BodyPrefix.Length - BodySuffix.Length) + BodySuffix;

    private static async Task<GenerateContentResponse> SendAsync(HttpContent content, Func<HttpContent, HttpContent>? wrap = null)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = wrap?.Invoke(content) ?? content }));
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions { ApiKey = "key", Model = Model }, httpClient);
        return await client.GenerateContentAsync("hello");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(4_095)]
    [InlineData(4_096)]
    [InlineData(4_097)]
    [InlineData(8_191)]
    [InlineData(8_192)]
    [InlineData(1_048_576)]
    public async Task BufferedRead_KnownLengthAroundPoolBucketBoundaries_ReadsTheWholeBody(int length)
    {
        var body = Encoding.UTF8.GetBytes(BodyOfExactLength(length));

        var response = await SendAsync(new ByteArrayContent(body));

        Assert.Equal(length - BodyPrefix.Length - BodySuffix.Length, response.Text!.Trim().Length);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(5_000L)]
    public async Task BufferedRead_ContentLengthUnderstatingTheBody_StillReadsEverything(long declaredLength)
    {
        var body = Encoding.UTF8.GetBytes(BodyOfExactLength(20_000));
        var content = new StreamContent(new MemoryStream(body));
        content.Headers.ContentLength = declaredLength;

        var response = await SendAsync(content);

        Assert.StartsWith("xxx", response.Text!.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BufferedRead_UnknownLengthBody_OneByteOverTheLimit_IsRejectedByLengthNotParsing()
    {
        var content = new StreamContent(new RepeatingStream(GeminiClient.MaxBufferedResponseBytes + 1L));

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => SendAsync(content));

        Assert.Contains("exceeded the maximum allowed length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BufferedRead_UnknownLengthBody_ExactlyAtTheLimit_ReachesTheParser()
    {
        var content = new StreamContent(new RepeatingStream(GeminiClient.MaxBufferedResponseBytes));

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => SendAsync(content));

        Assert.Contains("Failed to deserialize", error.Message, StringComparison.Ordinal);
    }

    private sealed class RepeatingStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var toRead = (int)Math.Min(buffer.Length, _remaining);
            buffer[..toRead].Fill((byte)'x');
            _remaining -= toRead;
            return toRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
    }

    #endregion

    #region Templates: a null trailing element must not crash mapping (pattern 2)

    [Fact]
    public void ToRequest_TemplateContentsEndingWithNull_MergesWithTheLastRealTurn()
    {
        IList<Content> templateContents = [new Content { Role = Role.User, Parts = [Part.FromText("template")] }, null!];
        var options = new ChatOptions { RawRepresentationFactory = _ => new GenerateContentRequest { Contents = templateContents } };
        using var client = new GeminiClient("key", Model);

        var request = ChatRequestMapper.ToRequest(
            [new ChatMessage(ChatRole.User, "next")],
            options,
            client.AsIChatClient(),
            Model,
            AIJsonUtilities.DefaultOptions);

        var turn = Assert.Single(request.Contents);
        Assert.Equal(["template", "next"], turn.Parts.Select(p => p.Text));
    }

    #endregion
}
