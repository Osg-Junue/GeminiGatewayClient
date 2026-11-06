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
public sealed class ValidationAndToolJsonTests
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
        { new List<KeyValuePair<string, int>> { new("a", 1) }, """{"result":[{"key":"a","value":1}]}""" },
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

    [Fact]
    public void ToResponseObject_CustomReadOnlyDictionary_IsWrittenAsAnObject()
    {
        var element = ToolJsonConverter.ToResponseObject(new ReadOnlyCounts(), AIJsonUtilities.DefaultOptions);

        Assert.Equal("""{"a":1}""", Raw(element));
    }

    [Fact]
    public void ToResponseObject_CustomReadOnlyDictionary_UsesSourceGeneratedMetadata()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = ReadOnlyCountsContext.Default };

        Assert.Equal("""{"a":1}""", Raw(ToolJsonConverter.ToResponseObject(new ReadOnlyCounts(), options)));
    }

    [Fact]
    public void ToResponseObject_CustomReadOnlyDictionaryWithoutMetadata_AsksForMetadata()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = GeminiJsonContext.Default };

        Assert.Throws<InvalidOperationException>(() => ToolJsonConverter.ToResponseObject(new ReadOnlyCounts(), options));
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

    #region Wire encoding: bodies stay semantically identical to the default encoder

    private static GenerateContentRequest RequestWithText(string text) =>
        new() { Contents = [new Content { Role = Role.User, Parts = [new Part { Text = text }] }] };

    private static string WireBody(GenerateContentRequest request) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(request, GeminiJsonContext.Wire.GenerateContentRequest));

    [Fact]
    public void WireEncoding_KeepsMultiByteTextAsUtf8()
    {
        var body = WireBody(RequestWithText("東京"));

        Assert.Contains("東京", body, StringComparison.Ordinal);
    }

    [Fact]
    public void WireEncoding_StillEscapesHtmlSensitiveAscii()
    {
        var body = WireBody(RequestWithText("<script>&'+"));

        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("&", body, StringComparison.Ordinal);
    }

    [Fact]
    public void WireEncoding_MatchesTheDefaultEncoderAfterParsing_ForRandomText()
    {
        string[] alphabet =
        [
            "a", "<", "&", "\"", "\\", "\n", "\u0000", "\u001F", "\u007F", "\u0080", "é", "あ", "🌸", "𠮷",
            "\u2028", "\u2029", "\uFEFF", "\uFFFD", "\uFFFF", "\uD83C", "\uDF38"
        ];
        var random = new Random(20261105);

        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var builder = new StringBuilder();
            for (var i = random.Next(0, 32); i > 0; i--)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var request = RequestWithText(builder.ToString());
            var viaDefault = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request, GeminiJsonContext.Default.GenerateContentRequest));
            var viaWire = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request, GeminiJsonContext.Wire.GenerateContentRequest));

            Assert.True(JsonElement.DeepEquals(viaDefault.RootElement, viaWire.RootElement), Convert.ToHexString(Encoding.Unicode.GetBytes(builder.ToString())));
        }
    }

    [Fact]
    public async Task WireEncoding_EveryRequestTypeSendsUtf8ThroughTheClient()
    {
        using var handler = new RecordingHandler("""{"candidates":[],"embedding":{"values":[0.1]},"embeddings":[{"values":[0.1]}]}""");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions { ApiKey = "key", Model = Model, EmbeddingModel = Model }, httpClient);

        await client.GenerateContentAsync(RequestWithText("東京"));
        Assert.Contains("東京", handler.LastRequestBody!, StringComparison.Ordinal);

        await foreach (var _ in client.StreamGenerateContentAsync(RequestWithText("京都")))
        {
        }

        Assert.Contains("京都", handler.LastRequestBody!, StringComparison.Ordinal);

        await client.EmbedContentAsync(EmbedContentRequest.FromText("大阪"));
        Assert.Contains("大阪", handler.LastRequestBody!, StringComparison.Ordinal);

        await client.BatchEmbedContentsAsync(new BatchEmbedContentsRequest { Requests = [EmbedContentRequest.FromText("奈良")] });
        Assert.Contains("奈良", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public void WireEncoding_ToolValuesProducedByTheConverter_AreNotLeftEscaped()
    {
        var request = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = Role.User,
                    Parts =
                    [
                        new Part
                        {
                            FunctionResponse = new FunctionResponse
                            {
                                Name = "f",
                                Response = ToolJsonConverter.ToResponseObject("東京", AIJsonUtilities.DefaultOptions)
                            }
                        }
                    ]
                }
            ]
        };

        Assert.Contains("東京", WireBody(request), StringComparison.Ordinal);
    }

    #endregion

    #region Argument reuse: the raw-byte shortcut never decides a difference on its own

    [Fact]
    public void ArgumentReuse_SameLengthDifferentBytes_RegeneratesArguments()
    {
        var message = ReplayedCall("""{"a":"xxxx"}""");
        Arguments(message)["a"] = Json("\"xxxy\"");

        Assert.Equal("""{"a":"xxxy"}""", WireArgs(message));
    }

    [Fact]
    public void ArgumentReuse_UndefinedElement_FailsAsItDoesWithoutRawArgs()
    {
        var message = ReplayedCall("""{"a":1}""");
        Arguments(message)["a"] = default(JsonElement);

        Assert.Throws<InvalidOperationException>(() => WireArgs(message));
    }

    [Fact]
    public void ArgumentReuse_ElementFromDisposedDocument_FailsLoudly()
    {
        var message = ReplayedCall("""{"a":1}""");
        var document = JsonDocument.Parse("1");
        Arguments(message)["a"] = document.RootElement;
        document.Dispose();

        Assert.Throws<ObjectDisposedException>(() => WireArgs(message));
    }

    #endregion

    #region Single-use sequences, template nulls, byte order marks, attached raw results, and validation order

    private sealed class OneShotSequence : IEnumerable<int>
    {
        private bool _consumed;

        public int Enumerations { get; private set; }

        public IEnumerator<int> GetEnumerator()
        {
            Enumerations++;
            int[] items = _consumed ? [] : [1, 2, 3];
            _consumed = true;
            return ((IEnumerable<int>)items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void ToResponseObject_SingleUseSequence_KeepsItsItemsAndIsEnumeratedOnce()
    {
        var sequence = new OneShotSequence();

        var converted = ToolJsonConverter.ToResponseObject(sequence, AIJsonUtilities.DefaultOptions);

        Assert.Equal("[1,2,3]", JsonSerializer.Serialize(converted.GetProperty("result"), GeminiJsonContext.Default.JsonElement));
        Assert.Equal(1, sequence.Enumerations);
    }

    [Fact]
    public void ToResponseObject_SingleUseSequenceInsideDictionary_KeepsItsItems()
    {
        var sequence = new OneShotSequence();

        var converted = ToolJsonConverter.ToResponseObject(new Dictionary<string, object?> { ["items"] = sequence }, AIJsonUtilities.DefaultOptions);

        Assert.Equal(3, converted.GetProperty("items").GetArrayLength());
        Assert.Equal(1, sequence.Enumerations);
    }

    [Fact]
    public void ToResponseObject_SequenceWithoutMetadata_IsStillWrittenByHand()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = GeminiJsonContext.Default };

        var converted = ToolJsonConverter.ToResponseObject(new OneShotSequence(), options);

        Assert.Equal("""{"result":[1,2,3]}""", Raw(converted));
    }

    public static TheoryData<Func<IList<Part>>> TemplatePartsWithNull() => new()
    {
        () => new[] { Part.FromText("ok"), null! },
        () => new List<Part> { Part.FromText("ok"), null! },
        () => new ReadOnlyCollection<Part>([Part.FromText("ok"), null!])
    };

    [Theory]
    [MemberData(nameof(TemplatePartsWithNull))]
    public void ToRequest_NullPartInTemplate_IsReportedForEveryCollectionType(Func<IList<Part>> createParts)
    {
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new GenerateContentRequest { Contents = [new Content { Role = Role.Model, Parts = createParts() }] }
        };
        using var client = new GeminiClient("key", Model);

        var request = ChatRequestMapper.ToRequest([new ChatMessage(ChatRole.User, "next")], options, client.AsIChatClient(), Model, AIJsonUtilities.DefaultOptions);

        Assert.Equal(
            "GenerateContentRequest.Contents[0].Parts[1] cannot be null. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => request.Validate()).Message);
    }

    [Fact]
    public async Task BufferedRead_BodyWithByteOrderMark_ParsesNormally()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = bom.Concat(Encoding.UTF8.GetBytes("""{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]}}]}""")).ToArray();

        var response = await SendAsync(new ByteArrayContent(body));

        Assert.Equal("ok", response.Text);
    }

    [Theory]
    [InlineData("""{"error":{"code":429,"message":"slow down","status":"RESOURCE_EXHAUSTED"}}""")]
    public async Task BufferedRead_ErrorBodyWithByteOrderMark_KeepsTheErrorDetails(string json)
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => SendAsync(new ByteArrayContent([.. bom, .. Encoding.UTF8.GetBytes(json)])));

        Assert.Contains("slow down", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<object, string> CollectionShapes() => new()
    {
        { new List<TimeOnly> { new(1, 2, 3) }, """{"result":["01:02:03"]}""" },
        { new List<Uri> { new("https://example.com/a") }, """{"result":["https://example.com/a"]}""" },
        { new[] { new TimeOnly(1, 2, 3) }, """{"result":["01:02:03"]}""" },
        { new HashSet<TimeOnly> { new(1, 2, 3) }, """{"result":["01:02:03"]}""" }
    };

    [Theory]
    [MemberData(nameof(CollectionShapes))]
    public void ToResponseObject_Collections_KeepTheirHandWrittenElementFormat(object result, string expected) =>
        Assert.Equal(expected, Raw(ToolJsonConverter.ToResponseObject(result, AIJsonUtilities.DefaultOptions)));

    [Fact]
    public void ToArguments_CollectionsNestedInArguments_KeepTheirHandWrittenElementFormat()
    {
        var arguments = new Dictionary<string, object?> { ["times"] = new List<TimeOnly> { new(1, 2, 3) } };

        Assert.Equal("""{"times":["01:02:03"]}""", Raw(ToolJsonConverter.ToArguments(arguments, AIJsonUtilities.DefaultOptions)!.Value));
    }

    [Fact]
    public void ToResponseObject_HonorsTheCallersNumberHandling()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.WriteAsString };

        Assert.Equal("""{"result":["1","2"]}""", Raw(ToolJsonConverter.ToResponseObject(new List<int> { 1, 2 }, options)));
    }

    public sealed class ObjectLikeSequence : IEnumerable<int>
    {
        public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)[1]).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ObjectLikeSequenceConverter : System.Text.Json.Serialization.JsonConverter<ObjectLikeSequence>
    {
        public override ObjectLikeSequence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, ObjectLikeSequence value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", 1);
            writer.WriteEndObject();
        }
    }

    [Fact]
    public void ToResponseObject_SequenceSerializedAsAnObject_IsNotWrapped()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.Converters.Add(new ObjectLikeSequenceConverter());

        var converted = ToolJsonConverter.ToResponseObject(new ObjectLikeSequence(), options);

        Assert.Equal(1, converted.GetProperty("count").GetInt32());
    }

    private static Part AttachedResponsePart() => new()
    {
        ThoughtSignature = "sig",
        ExtensionData = new Dictionary<string, JsonElement> { ["partExt"] = Json("1") },
        FunctionResponse = new FunctionResponse
        {
            Id = "server-id",
            Name = "real_tool",
            Response = Json("""{"stale":true}"""),
            ExtensionData = new Dictionary<string, JsonElement> { ["willContinue"] = Json("true") }
        }
    };

    private static Part MapResult(FunctionResultContent result, params ChatMessage[] history)
    {
        using var client = new GeminiClient("key", Model);
        var request = ChatRequestMapper.ToRequest(
            [.. history, new ChatMessage(ChatRole.Tool, [result])],
            null,
            client.AsIChatClient(),
            Model,
            AIJsonUtilities.DefaultOptions);
        return request.Contents[^1].Parts[^1];
    }

    [Theory]
    [InlineData("")]
    [InlineData("stray text")]
    public void ToRequest_TextOnAttachedRaw_IsDroppedSoTheRequestStaysValid(string text)
    {
        var raw = AttachedResponsePart();
        raw.Text = text;

        var part = MapResult(new FunctionResultContent("local-id", "fresh") { RawRepresentation = raw });

        Assert.Null(part.Text);
        Assert.Null(part.GetValidationError());
    }

    [Fact]
    public void ToResponseObject_ReferenceHandlerPreserve_KeepsReferenceMetadataAndHandlesCycles()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.Preserve };
        var cycle = new List<object?>();
        cycle.Add(cycle);

        var plain = ToolJsonConverter.ToResponseObject(new List<int> { 1 }, options);
        var cyclic = ToolJsonConverter.ToResponseObject(cycle, options);

        Assert.Equal("1", plain.GetProperty("$id").GetString());
        Assert.Equal(1, plain.GetProperty("$values").GetArrayLength());
        Assert.Equal("1", cyclic.GetProperty("$values")[0].GetProperty("$ref").GetString());
    }

    [Fact]
    public void ToResponseObject_ByteArray_IsWrittenAsBase64()
    {
        Assert.Equal("""{"result":"AQID"}""", Raw(ToolJsonConverter.ToResponseObject(new byte[] { 1, 2, 3 }, AIJsonUtilities.DefaultOptions)));
    }

    [Fact]
    public void ToRequest_RawPartWithoutFunctionResponse_FallsBackToTheResult()
    {
        var part = MapResult(new FunctionResultContent("local-id", "fresh") { RawRepresentation = new Part { ThoughtSignature = "sig" } });

        Assert.Equal("local-id", part.FunctionResponse!.Name);
        Assert.Equal("""{"result":"fresh"}""", Raw(part.FunctionResponse.Response!.Value));
    }

    [Fact]
    public void ToRequest_AttachedRawDoesNotShareStateWithTheRequest()
    {
        var raw = AttachedResponsePart();

        MapResult(new FunctionResultContent("local-id", "fresh") { RawRepresentation = raw });

        Assert.Equal("""{"stale":true}""", Raw(raw.FunctionResponse!.Response!.Value));
    }

    [Fact]
    public void WireEncoding_SupplementaryCharacters_StayEscapedAndRoundTrip()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(RequestWithText("🌸"), GeminiJsonContext.Wire.GenerateContentRequest);

        Assert.DoesNotContain("🌸", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal("🌸", JsonSerializer.Deserialize(bytes, GeminiJsonContext.Default.GenerateContentRequest)!.Contents[0].Parts[0].Text);
    }

    [Fact]
    public void Validate_SchemaWithSeveralViolations_ReportsItemsThenPropertiesThenAnyOf()
    {
        var unknown = new Schema { Type = SchemaType.Unknown };
        var schema = new Schema
        {
            Type = SchemaType.Object,
            Items = unknown,
            Properties = new Dictionary<string, Schema> { ["a"] = unknown },
            AnyOf = [unknown]
        };

        Assert.StartsWith("GenerationConfig.ResponseSchema.Items.", SchemaError(schema), StringComparison.Ordinal);

        schema.Items = null;
        Assert.StartsWith("GenerationConfig.ResponseSchema.Properties['a'].", SchemaError(schema), StringComparison.Ordinal);

        schema.Properties = null;
        Assert.StartsWith("GenerationConfig.ResponseSchema.AnyOf[0].", SchemaError(schema), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_FunctionDeclarationWithInvalidSchemas_ReportsParametersFirst()
    {
        var declaration = new FunctionDeclaration
        {
            Name = "f",
            Parameters = new Schema { Type = SchemaType.Unknown },
            Response = new Schema { Type = SchemaType.Unknown }
        };

        Assert.StartsWith("FunctionDeclaration('f').Parameters.", Assert.Throws<ArgumentException>(() => declaration.Validate()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToRequest_FunctionResultChangedAfterRawWasAttached_SendsTheCurrentResult()
    {
        var stale = Part.FromFunctionResponse("f", Json("""{"stale":true}"""), "c1");
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "fresh") { RawRepresentation = stale }]);
        using var client = new GeminiClient("key", Model);

        var request = ChatRequestMapper.ToRequest([message], null, client.AsIChatClient(), Model, AIJsonUtilities.DefaultOptions);

        Assert.Equal("""{"result":"fresh"}""", Raw(request.Contents[0].Parts[0].FunctionResponse!.Response!.Value));
    }

    [Fact]
    public void Validate_EmbedRequestWithSeveralViolations_ReportsContentThenTaskTypeThenDimensionality()
    {
        var everythingWrong = EmbedContentRequest.FromText("x", TaskType.Unknown, outputDimensionality: 0);
        everythingWrong.Content.Role = Role.Unknown;
        Assert.StartsWith("EmbedContentRequest.Content.Role ", Assert.Throws<ArgumentException>(() => everythingWrong.Validate()).Message, StringComparison.Ordinal);

        var wrongTaskAndDimensions = EmbedContentRequest.FromText("x", TaskType.Unknown, outputDimensionality: 0);
        Assert.StartsWith("EmbedContentRequest.TaskType ", Assert.Throws<ArgumentException>(() => wrongTaskAndDimensions.Validate()).Message, StringComparison.Ordinal);

        var wrongTitleAndDimensions = EmbedContentRequest.FromText("x", TaskType.RetrievalQuery, "title", 0);
        Assert.StartsWith("EmbedContentRequest.Title ", Assert.Throws<ArgumentException>(() => wrongTitleAndDimensions.Validate()).Message, StringComparison.Ordinal);
    }

    private static string SchemaError(Schema schema) =>
        Assert.Throws<ArgumentException>(() => new GenerationConfig { ResponseSchema = schema }.Validate()).Message;

    [Fact]
    public void Validate_Schema_ReportsExactNestedPaths()
    {
        var unknown = new Schema { Type = SchemaType.Unknown };
        var valid = new Schema { Type = SchemaType.String };

        Assert.Equal(
            "GenerationConfig.ResponseSchema.Type cannot be SchemaType.Unknown. (Parameter 'request')",
            SchemaError(unknown));
        Assert.Equal(
            "GenerationConfig.ResponseSchema.Default cannot be JsonValueKind.Undefined. (Parameter 'request')",
            SchemaError(new Schema { Type = SchemaType.String, Default = default(JsonElement) }));
        Assert.Equal(
            "GenerationConfig.ResponseSchema.Items.Items.Type cannot be SchemaType.Unknown. (Parameter 'request')",
            SchemaError(new Schema { Type = SchemaType.Array, Items = new Schema { Type = SchemaType.Array, Items = unknown } }));
        Assert.Equal(
            "GenerationConfig.ResponseSchema.Properties['a'].Type cannot be SchemaType.Unknown. (Parameter 'request')",
            SchemaError(new Schema { Type = SchemaType.Object, Properties = new Dictionary<string, Schema> { ["a"] = unknown } }));
        Assert.Equal(
            "GenerationConfig.ResponseSchema.Properties['a'] cannot be null. (Parameter 'request')",
            SchemaError(new Schema { Type = SchemaType.Object, Properties = new Dictionary<string, Schema> { ["a"] = null! } }));
        Assert.Equal(
            "GenerationConfig.ResponseSchema.AnyOf[0].Type cannot be SchemaType.Unknown. (Parameter 'request')",
            SchemaError(new Schema { Type = SchemaType.Object, AnyOf = [unknown, valid] }));
        Assert.Equal(
            "GenerationConfig.ResponseSchema.AnyOf[1] cannot be null. (Parameter 'request')",
            SchemaError(new Schema { Type = SchemaType.Object, AnyOf = [valid, null!] }));

        var declaration = new FunctionDeclaration { Name = "f", Parameters = unknown };
        Assert.Equal(
            "FunctionDeclaration('f').Parameters.Type cannot be SchemaType.Unknown. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => declaration.Validate()).Message);

        var response = new FunctionDeclaration { Name = "f", Response = new Schema { Type = SchemaType.Array, Items = unknown } };
        Assert.Equal(
            "FunctionDeclaration('f').Response.Items.Type cannot be SchemaType.Unknown. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => response.Validate()).Message);
    }

    [Fact]
    public void Validate_SelfReferencingSchema_Terminates()
    {
        var schema = new Schema { Type = SchemaType.Object };
        schema.Properties = new Dictionary<string, Schema> { ["self"] = schema };

        new GenerationConfig { ResponseSchema = schema }.Validate();
    }

    #endregion

    #region Templates: null contents are reported at their template index (patterns 1 and 2)

    private static GenerateContentRequest MapWithTemplateContents(IList<Content> templateContents, params ChatMessage[] messages)
    {
        var options = new ChatOptions { RawRepresentationFactory = _ => new GenerateContentRequest { Contents = templateContents } };
        using var client = new GeminiClient("key", Model);
        return ChatRequestMapper.ToRequest(messages, options, client.AsIChatClient(), Model, AIJsonUtilities.DefaultOptions);
    }

    public static TheoryData<Func<IList<Content>>, int> TemplateContentsWithNull() => new()
    {
        { () => new List<Content> { null! }, 0 },
        { () => new List<Content> { null!, Content.FromText("template") }, 0 },
        { () => new List<Content> { Content.FromText("template"), null! }, 1 },
        { () => new List<Content> { Content.FromText("a"), null!, Content.FromText("b") }, 1 },
        { () => new[] { Content.FromText("template"), null! }, 1 },
        { () => new ReadOnlyCollection<Content>([Content.FromText("template"), null!]), 1 }
    };

    [Theory]
    [MemberData(nameof(TemplateContentsWithNull))]
    public void ToRequest_NullContentInTemplate_IsReportedAtItsTemplateIndex(Func<IList<Content>> createContents, int expectedIndex)
    {
        var request = MapWithTemplateContents(createContents(), new ChatMessage(ChatRole.User, "next"));

        Assert.Equal(
            $"GenerateContentRequest.Contents[{expectedIndex}] cannot be null. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => request.Validate()).Message);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    [InlineData("system")]
    public void ToRequest_MessageAfterNullTemplateTail_DoesNotMergeIntoTheNull(string role)
    {
        var request = MapWithTemplateContents(
            new List<Content> { Content.FromText("template"), null! },
            new ChatMessage(new ChatRole(role), "next"));

        Assert.Null(request.Contents[1]);
    }

    [Fact]
    public async Task GetResponse_NullContentInTemplate_FailsBeforeAnythingIsSent()
    {
        using var handler = new RecordingHandler("""{"candidates":[]}""");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions { ApiKey = "key", Model = Model }, httpClient);
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new GenerateContentRequest { Contents = [Content.FromText("template"), null!] }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.AsIChatClient().GetResponseAsync("next", options));

        Assert.Empty(handler.Requests);
    }

    private static GenerateContentRequest MapWithTemplate(GenerateContentRequest template, ChatOptions? additionalOptions = null)
    {
        var options = additionalOptions ?? new ChatOptions();
        options.RawRepresentationFactory = _ => template;
        using var client = new GeminiClient("key", Model);
        return ChatRequestMapper.ToRequest([new ChatMessage(ChatRole.User, "next")], options, client.AsIChatClient(), Model, AIJsonUtilities.DefaultOptions);
    }

    private static Tool ToolWith(string name) => new() { FunctionDeclarations = [new FunctionDeclaration { Name = name }] };

    private static SafetySetting Setting() => new() { Category = HarmCategory.Harassment, Threshold = HarmBlockThreshold.BlockNone };

    public static TheoryData<Func<IList<Tool>>, int> TemplateToolsWithNull() => new()
    {
        { () => new List<Tool> { null! }, 0 },
        { () => new List<Tool> { ToolWith("a"), null! }, 1 },
        { () => new[] { ToolWith("a"), null! }, 1 },
        { () => new ReadOnlyCollection<Tool>([null!, ToolWith("a")]), 0 }
    };

    [Theory]
    [MemberData(nameof(TemplateToolsWithNull))]
    public void ToRequest_NullToolInTemplate_IsReportedAtItsTemplateIndex(Func<IList<Tool>> createTools, int expectedIndex)
    {
        var request = MapWithTemplate(new GenerateContentRequest { Tools = createTools() });

        Assert.Equal(
            $"GenerateContentRequest.Tools[{expectedIndex}] cannot be null. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => request.Validate()).Message);
    }

    [Fact]
    public void ToRequest_NullToolInTemplate_SurvivesMergingChatOptionTools()
    {
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create((string a) => a, "added"), new HostedWebSearchTool()] };

        var request = MapWithTemplate(new GenerateContentRequest { Tools = [ToolWith("existing"), null!] }, options);

        Assert.Equal(
            "GenerateContentRequest.Tools[1] cannot be null. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => request.Validate()).Message);
    }

    [Fact]
    public void ToRequest_NullFunctionDeclarationInTemplate_IsReported()
    {
        var template = new GenerateContentRequest { Tools = [new Tool { FunctionDeclarations = [new FunctionDeclaration { Name = "a" }, null!] }] };
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create((string a) => a, "added")] };

        foreach (var request in new[] { MapWithTemplate(template), MapWithTemplate(template, options) })
        {
            Assert.Equal(
                "Tool.FunctionDeclarations cannot contain null elements. (Parameter 'request')",
                Assert.Throws<ArgumentException>(() => request.Validate()).Message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ToRequest_NullSafetySettingInTemplate_IsReportedAtItsTemplateIndex(int nullIndex)
    {
        IList<SafetySetting> settings = [Setting(), Setting()];
        settings[nullIndex] = null!;

        var request = MapWithTemplate(new GenerateContentRequest { SafetySettings = settings });

        Assert.Equal(
            $"GenerateContentRequest.SafetySettings[{nullIndex}] cannot be null. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => request.Validate()).Message);
    }

    [Fact]
    public void ToRequest_NullAnyOfInTemplateSchema_IsReported()
    {
        var schema = new Schema { Type = SchemaType.Object, AnyOf = [new Schema { Type = SchemaType.String }, null!] };
        var template = new GenerateContentRequest { GenerationConfig = new GenerationConfig { ResponseSchema = schema } };

        var request = MapWithTemplate(template);

        Assert.Equal(
            "GenerationConfig.ResponseSchema.AnyOf[1] cannot be null. (Parameter 'request')",
            Assert.Throws<ArgumentException>(() => request.Validate()).Message);
    }

    [Fact]
    public void ToModelContent_NullsInServerResponses_StayTolerated()
    {
        var chunk = new GenerateContentResponse
        {
            Candidates = [new Candidate { Content = new Content { Role = Role.Model, Parts = [null!, Part.FromText("ok")] } }]
        };

        var content = new[] { null!, chunk }.ToModelContent();

        Assert.Equal("ok", Assert.Single(content!.Parts).Text);
    }

    #endregion

    #region Inline data tolerates keys the API does not guarantee

    private static GenerateContentResponse ParseResponse(string partsJson) => JsonSerializer.Deserialize(
        $$$"""{"candidates":[{"content":{"role":"model","parts":[{{{partsJson}}}]}}]}""",
        GeminiJsonContext.Default.GenerateContentResponse)!;

    [Fact]
    public void TryGetModelContent_EmptyTextNextToOtherData_IsNotResent()
    {
        var response = ParseResponse("""{"text":"","functionCall":{"name":"f"},"thoughtSignature":"sig"}""");

        Assert.True(response.TryGetModelContent(out var content));

        var part = Assert.Single(content.Parts);
        Assert.Null(part.Text);
        Assert.Equal("f", part.FunctionCall?.Name);
        Assert.Equal("sig", part.ThoughtSignature);
    }

    [Fact]
    public void TryGetModelContent_EmptyTextWithOnlyASignature_KeepsTheText()
    {
        var response = ParseResponse("""{"text":"","thoughtSignature":"sig"}""");

        Assert.True(response.TryGetModelContent(out var content));

        Assert.Equal(string.Empty, Assert.Single(content.Parts).Text);
    }

    [Fact]
    public void InlineData_WithoutMimeType_KeepsTheResponseAndFallsBackToOctetStream()
    {
        var response = ParseResponse("""{"text":"ok"},{"inlineData":{"data":"AQID"}}""");

        var contents = ChatResponseMapper.ToChatResponse(response, Model).Messages[0].Contents;

        Assert.Equal("ok", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal(MimeTypes.ApplicationOctetStream, Assert.IsType<DataContent>(contents[1]).MediaType);
    }

    [Fact]
    public void InlineData_WithoutData_KeepsTheRestOfTheResponse()
    {
        var response = ParseResponse("""{"text":"ok"},{"inlineData":{"mimeType":"image/png"}}""");

        var content = Assert.Single(ChatResponseMapper.ToChatResponse(response, Model).Messages[0].Contents);

        Assert.Equal("ok", Assert.IsType<TextContent>(content).Text);
    }

    [Fact]
    public void InlineData_WithoutMimeType_CanBeReplayedOnTheNextTurn()
    {
        var message = ChatResponseMapper.ToChatResponse(ParseResponse("""{"inlineData":{"data":"AQID"}}"""), Model).Messages[0];

        var request = MapMessages(message);
        request.Validate();

        var blob = request.Contents[0].Parts[0].InlineData!;
        Assert.Equal(MimeTypes.ApplicationOctetStream, blob.MimeType);
        Assert.Equal([1, 2, 3], blob.Data);
    }

    [Theory]
    [InlineData("""{"inlineData":{"data":"AQID"}}""", ".InlineData.MimeType cannot be null or whitespace.")]
    [InlineData("""{"inlineData":{"mimeType":"image/png"}}""", ".InlineData.Data cannot be null or empty.")]
    [InlineData("""{"inlineData":{}}""", ".InlineData.MimeType cannot be null or whitespace.")]
    public void InlineData_FromAPartialResponse_IsStillRejectedWhenSent(string partJson, string reason)
    {
        var part = ParseResponse(partJson).Candidates[0].Content!.Parts[0];

        Assert.Equal(reason, part.GetValidationError());
    }

    [Fact]
    public void InlineData_NullJsonValues_BecomeEmptyInsteadOfNull()
    {
        var blob = ParseResponse("""{"inlineData":{"mimeType":null,"data":null}}""").Candidates[0].Content!.Parts[0].InlineData!;

        Assert.Equal(string.Empty, blob.MimeType);
        Assert.Empty(blob.Data);
    }

    #endregion

    #region URIs reach the wire exactly as supplied

    private static GenerateContentRequest MapMessages(params ChatMessage[] messages)
    {
        using var client = new GeminiClient("key", Model);
        return ChatRequestMapper.ToRequest(messages, null, client.AsIChatClient(), Model, AIJsonUtilities.DefaultOptions);
    }

    [Theory]
    [InlineData("https://example.com/a%20b.pdf")]
    [InlineData("https://example.com/%E3%81%82.pdf")]
    [InlineData("https://example.com/a%2Fb.pdf?x=1%262#f")]
    [InlineData("gs://bucket/a%20b.pdf")]
    [InlineData("files/abc123")]
    public void UriContent_IsSentExactlyAsSupplied(string uri)
    {
        var content = new UriContent(new Uri(uri, UriKind.RelativeOrAbsolute), "application/pdf");

        var request = MapMessages(new ChatMessage(ChatRole.User, [content]));

        Assert.Equal(uri, request.Contents[0].Parts[0].FileData!.FileUri);
    }

    [Fact]
    public void UriContent_ChangedAfterMapping_IsNotMistakenForTheRawPart()
    {
        var response = ParseResponse("""{"fileData":{"fileUri":"https://example.com/a%20b.pdf","mimeType":"application/pdf"}}""");
        var message = ChatResponseMapper.ToChatResponse(response, Model).Messages[0];
        message.Contents[0] = new UriContent(new Uri("https://example.com/a%20c.pdf"), "application/pdf") { RawRepresentation = message.Contents[0].RawRepresentation };

        Assert.Equal("https://example.com/a%20c.pdf", MapMessages(message).Contents[0].Parts[0].FileData!.FileUri);
    }

    [Fact]
    public void ToResponseObject_UriValue_KeepsItsEscapes()
    {
        var converted = ToolJsonConverter.ToResponseObject(new Uri("https://example.com/a%20b"), new JsonSerializerOptions { TypeInfoResolver = GeminiJsonContext.Default });

        Assert.Equal("https://example.com/a%20b", converted.GetProperty("result").GetString());
    }

    #endregion

    #region Connection failures while reading a body surface as HttpRequestException

    private sealed class FailingStream(byte[] prefix, Exception failure) : Stream
    {
        private int _position;

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
            if (_position == prefix.Length)
            {
                throw failure;
            }

            var count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
    }

    public static TheoryData<Exception, HttpRequestError> BodyFailures() => new()
    {
        { new HttpIOException(HttpRequestError.ResponseEnded), HttpRequestError.ResponseEnded },
        { new HttpIOException(HttpRequestError.InvalidResponse), HttpRequestError.InvalidResponse },
        { new IOException("connection reset"), HttpRequestError.ResponseEnded }
    };

    #endregion

    #region Parts without a chat mapping survive the IChatClient round trip

    private const string ToolPartsJson = """{"toolCall":{"id":"1","toolType":"GOOGLE_SEARCH_WEB"}},{"toolResponse":{"id":"1","response":{"ok":true}}},{"text":"done"}""";

    [Fact]
    public void UnmappedParts_AreKeptInOrderAlongsideText()
    {
        var contents = ChatResponseMapper.ToChatResponse(ParseResponse(ToolPartsJson), Model).Messages[0].Contents;

        Assert.Equal(3, contents.Count);
        Assert.True(Assert.IsType<Part>(contents[0].RawRepresentation).ExtensionData!.ContainsKey("toolCall"));
        Assert.True(Assert.IsType<Part>(contents[1].RawRepresentation).ExtensionData!.ContainsKey("toolResponse"));
        Assert.Equal("done", Assert.IsType<TextContent>(contents[2]).Text);
    }

    [Fact]
    public void UnmappedParts_ReplayInOrderAndPassValidation()
    {
        var message = ChatResponseMapper.ToChatResponse(ParseResponse(ToolPartsJson), Model).Messages[0];

        var request = MapMessages(message);
        request.Validate();

        var parts = request.Contents[0].Parts;
        Assert.Equal(3, parts.Count);
        Assert.Equal("""{"id":"1","toolType":"GOOGLE_SEARCH_WEB"}""", Raw(parts[0].ExtensionData!["toolCall"]));
        Assert.Equal("""{"id":"1","response":{"ok":true}}""", Raw(parts[1].ExtensionData!["toolResponse"]));
        Assert.Equal("done", parts[2].Text);
    }

    [Fact]
    public void UnmappedPart_WithSignature_StaysSeparateFromTheTextBeforeIt()
    {
        var response = ParseResponse("""{"text":"a"},{"toolCall":{"id":"1"},"thoughtSignature":"s"}""");
        var message = ChatResponseMapper.ToChatResponse(response, Model).Messages[0];

        var parts = MapMessages(message).Contents[0].Parts;

        Assert.Equal(2, parts.Count);
        Assert.Equal("a", parts[0].Text);
        Assert.Null(parts[0].ThoughtSignature);
        Assert.Equal("s", parts[1].ThoughtSignature);
        Assert.True(parts[1].ExtensionData!.ContainsKey("toolCall"));
    }

    [Fact]
    public void UnmappedPart_SharingASignatureWithTheTextBeforeIt_IsNotDropped()
    {
        var response = ParseResponse("""{"text":"a","thoughtSignature":"s"},{"thoughtSignature":"s","custom":1}""");
        var message = ChatResponseMapper.ToChatResponse(response, Model).Messages[0];

        var parts = MapMessages(message).Contents[0].Parts;

        Assert.Equal(2, parts.Count);
        Assert.True(parts[1].ExtensionData!.ContainsKey("custom"));
    }

    [Fact]
    public void SignatureOnlyPart_WithoutExtensionData_KeepsItsExistingMapping()
    {
        var contents = ChatResponseMapper.ToChatResponse(ParseResponse("""{"thoughtSignature":"s"}"""), Model).Messages[0].Contents;

        Assert.Equal("s", Assert.IsType<TextReasoningContent>(Assert.Single(contents)).ProtectedData);
    }

    [Theory]
    [InlineData("""{"fileData":{"fileUri":""},"custom":1}""")]
    [InlineData("""{"inlineData":{"mimeType":"a/b"},"custom":1}""")]
    [InlineData("""{"inlineData":{},"custom":1}""")]
    public void UnmappedPart_ThatCouldNotBeResent_IsIgnoredInsteadOfBreakingTheConversation(string partJson)
    {
        var response = ParseResponse(partJson + """,{"text":"ok"}""");
        var message = ChatResponseMapper.ToChatResponse(response, Model).Messages[0];

        Assert.Equal("ok", Assert.IsType<TextContent>(Assert.Single(message.Contents)).Text);
        MapMessages(message).Validate();
    }

    [Fact]
    public void UnmappedParts_DoNotAlterTextOrFinishReason()
    {
        var mapped = ChatResponseMapper.ToChatResponse(ParseResponse(ToolPartsJson), Model);

        Assert.Equal("done", mapped.Text);
        Assert.NotEqual(ChatFinishReason.ToolCalls, mapped.FinishReason);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"text":""}""")]
    public void EmptyParts_AreStillOmitted(string partJson)
    {
        Assert.Empty(ChatResponseMapper.ToChatResponse(ParseResponse(partJson), Model).Messages[0].Contents);
    }

    #endregion
}

internal sealed class ReadOnlyCounts : IReadOnlyDictionary<string, int>
{
    private readonly Dictionary<string, int> _counts = new() { ["a"] = 1 };

    public int this[string key] => _counts[key];

    public IEnumerable<string> Keys => _counts.Keys;

    public IEnumerable<int> Values => _counts.Values;

    public int Count => _counts.Count;

    public bool ContainsKey(string key) => _counts.ContainsKey(key);

    public bool TryGetValue(string key, out int value) => _counts.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _counts.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

[System.Text.Json.Serialization.JsonSerializable(typeof(ReadOnlyCounts))]
internal sealed partial class ReadOnlyCountsContext : System.Text.Json.Serialization.JsonSerializerContext;
