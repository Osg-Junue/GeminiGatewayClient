using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GeminiGatewayClient.Models;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// How response bodies are read: event-stream framing, errors sent with a 2xx status, timeouts, and lost connections.
/// </summary>
public class TransportTests
{
    private const string Chunk = """{"candidates":[{"content":{"role":"model","parts":[{"text":"hi"}]}}]}""";

    private static GenerateContentRequest Request() => new() { Contents = [Content.FromText("hi")] };

    private static GeminiClient CreateClient(HttpMessageHandler handler, TimeSpan? idleTimeout = null) =>
        new(new GeminiClientOptions { ApiKey = "test-key", Model = "gemini-2.5-flash", StreamIdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30) }, new HttpClient(handler));

    private static async Task<List<string?>> StreamTextsAsync(HttpMessageHandler handler, TimeSpan? idleTimeout = null)
    {
        using var client = CreateClient(handler, idleTimeout);

        // A test of a stall fails here rather than hanging the run if the idle timeout stops working.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var texts = new List<string?>();
        await foreach (var chunk in client.StreamGenerateContentAsync(Request(), cancellationToken: guard.Token))
        {
            texts.Add(chunk.Text);
        }

        return texts;
    }

    private static StubHandler Stream(Stream body, string mediaType = "text/event-stream", HttpStatusCode status = HttpStatusCode.OK) =>
        new(() =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(status) { Content = content };
        });

    private static StubHandler Body(string body, string mediaType = "text/event-stream", HttpStatusCode status = HttpStatusCode.OK) =>
        Stream(new MemoryStream(Encoding.UTF8.GetBytes(body)), mediaType, status);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task Stream_AcceptsEveryLineEnding(string newline)
    {
        var body = $"data: {Chunk}{newline}{newline}data: {Chunk}{newline}{newline}";

        Assert.Equal(["hi", "hi"], await StreamTextsAsync(Body(body)));
    }

    [Fact]
    public async Task Stream_IgnoresCommentsOtherFieldsAndTheDoneMarker()
    {
        var body = $": keep-alive\n\nevent: message\nid: 1\ndata: {Chunk}\n\ndata: [DONE]\n\n";

        Assert.Equal(["hi"], await StreamTextsAsync(Body(body)));
    }

    [Fact]
    public async Task Stream_JoinsAnEventSplitOverDataLines()
    {
        const string body = """
            data: {"candidates":[{"content":{"role":"model",
            data: "parts":[{"text":"hi"}]}}]}


            """;

        Assert.Equal(["hi"], await StreamTextsAsync(Body(body.Replace("\r\n", "\n"))));
    }

    [Fact]
    public async Task Stream_KeepsAMultibyteCharacterSplitAcrossReads()
    {
        var bytes = Encoding.UTF8.GetBytes("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"🌸あ\"}]}}]}\n\n");

        Assert.Equal(["🌸あ"], await StreamTextsAsync(Stream(new TrickleStream(bytes))));
    }

    [Theory]
    [InlineData("text/event-stream")]
    [InlineData("application/json")]
    [InlineData("application/octet-stream")]
    public async Task Stream_IsRecognizedByItsBodyWhateverTheContentType(string mediaType)
    {
        Assert.Equal(["hi", "hi"], await StreamTextsAsync(Body($"data: {Chunk}\n\ndata: {Chunk}\n\n", mediaType)));
    }

    [Theory]
    [InlineData("text/event-stream")]
    [InlineData("application/json")]
    public async Task Stream_RawJsonError_KeepsItsDetailsWhateverTheContentType(string mediaType)
    {
        const string body = """{"error":{"code":503,"message":"m","status":"UNAVAILABLE"}}""";

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body, mediaType)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("UNAVAILABLE", error.ErrorStatus);
    }

    [Fact]
    public async Task Stream_JsonErrorAfterAFirstReadOfOnlyWhitespace_IsStillRecognized()
    {
        var body = new ChunkedStream("\n", """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED"}}""");

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Stream(body)));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    [Theory]
    [InlineData("data: {0}")]
    [InlineData("data: {0}\n")]
    public async Task Stream_KeepsAFinalEventWithoutABlankLine(string format)
    {
        Assert.Equal(["hi"], await StreamTextsAsync(Body(string.Format(format, Chunk))));
    }

    [Fact]
    public async Task Stream_CutOffInsideAnEvent_IsAnError()
    {
        await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body($"data: {Chunk}\n\ndata: {{\"candi")));
    }

    [Theory]
    [InlineData("""{"error":{"code":"invalid_api_key","message":"bad key"}}""")]
    [InlineData("""{"error":{"code":"401","message":"bad key"}}""")]
    [InlineData("""{"error":{"code":null,"message":"bad key"}}""")]
    [InlineData("""[{"error":{"code":401,"message":"bad key"}}]""")]
    public async Task Generate_GatewayErrorShapes_KeepTheStatusAndMessage(string body)
    {
        using var client = CreateClient(Body(body, "application/json", HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(Request()));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal("bad key", error.ErrorMessage);
    }

    [Theory]
    [InlineData("shift_jis")]
    [InlineData("bogus")]
    public async Task Generate_ErrorBodyWithAWrongCharset_KeepsItsDetails(string charset)
    {
        var handler = new StubHandler(() =>
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"error":{"code":429,"message":"日本語","status":"RESOURCE_EXHAUSTED"}}"""));
            content.Headers.TryAddWithoutValidation("Content-Type", $"application/json; charset={charset}");
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content };
        });
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(Request()));

        Assert.Equal("日本語", error.ErrorMessage);
        Assert.Equal("RESOURCE_EXHAUSTED", error.ErrorStatus);
    }

    [Fact]
    public async Task Generate_LongErrorBody_CutInsideASurrogatePair_KeepsTheDetails()
    {
        var padding = new string('x', (64 * 1024) - 60);
        var body = "{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\",\"message\":\"" + padding + "🌸🌸🌸🌸\"}}";
        using var client = CreateClient(Body(body, "application/json", HttpStatusCode.ServiceUnavailable));

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(Request()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("UNAVAILABLE", error.ErrorStatus);
        Assert.Equal(64 * 1024, error.ErrorContent?.Length);
    }

    [Fact]
    public async Task Stream_ErrorEventWithATextCode_IsReportedAsAFailure()
    {
        const string body = """data: {"error":{"code":"quota","message":"m"}}""";

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body + "\n\n")));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.Equal("m", error.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData(": keep-alive\n\n")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data:\n\n")]
    [InlineData("data: \n\ndata: [DONE]\n\n")]
    [InlineData(": keep-alive\n\nevent: ping\ndata:\n\ndata: [DONE]\n\n")]
    public async Task Stream_WithoutAnyEvent_IsReportedAsEmpty(string body)
    {
        await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body)));
    }

    private const string TextResponsePrefix = "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"";
    private const string TextResponseSuffix = "\"}]}}]}";

    private static string ResponseOfLength(int length) =>
        TextResponsePrefix + new string('x', length - TextResponsePrefix.Length - TextResponseSuffix.Length) + TextResponseSuffix;

    [Fact]
    public async Task Stream_ErrorEvent_SurfacesTheGoogleErrorAfterEarlierChunks()
    {
        var body = $"data: {Chunk}\n\ndata: {{\"error\":{{\"code\":503,\"message\":\"overloaded\",\"status\":\"UNAVAILABLE\"}}}}\n\n";
        using var client = CreateClient(Body(body));
        var received = 0;

        var error = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in client.StreamGenerateContentAsync(Request()))
            {
                received++;
            }
        });

        Assert.Equal(1, received);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("UNAVAILABLE", error.ErrorStatus);
    }

    [Fact]
    public async Task Stream_GatewayAnsweringWithAJsonError_SurfacesTheError()
    {
        const string body = """{"error":{"code":429,"message":"slow down","status":"RESOURCE_EXHAUSTED"}}""";

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body, "application/json")));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal("slow down", error.ErrorMessage);
    }

    private const string ArrayError =
        """[{"error":{"code":429,"message":"slow down","status":"RESOURCE_EXHAUSTED","details":[{"retryDelay":"15s"}]}}]""";

    private static void AssertRetryableQuotaError(GeminiApiException error)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal("RESOURCE_EXHAUSTED", error.ErrorStatus);
        Assert.Equal("slow down", error.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(15), error.RetryAfter);
    }

    [Fact]
    public async Task Stream_ErrorWrappedInAnArrayWithA2xxStatus_KeepsTheErrorDetails()
    {
        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(ArrayError, "application/json")));

        AssertRetryableQuotaError(error);
        Assert.StartsWith("""{"code":429,""", error.ErrorContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_ErrorWrappedInAnArrayWithA2xxStatus_KeepsTheErrorDetails()
    {
        using var client = CreateClient(Body(ArrayError, "application/json"));

        AssertRetryableQuotaError(await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(Request())));
    }

    [Fact]
    public async Task Stream_PlainJsonErrorAfterKeepAliveComments_KeepsTheErrorDetails()
    {
        var body = $": keep-alive\n\n: keep-alive\n\n{ArrayError}\n";

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body, "application/json")));

        AssertRetryableQuotaError(error);
    }

    [Fact]
    public async Task Stream_PrettyPrintedJsonErrorAfterAKeepAliveComment_KeepsTheErrorDetails()
    {
        const string body = ": keep-alive\n\n{\n  \"error\": {\n    \"code\": 503,\n    \"status\": \"UNAVAILABLE\"\n  }\n}\n";

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body, "application/json")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public async Task Stream_PlainTextBody_KeepsTheBodyForDiagnosis()
    {
        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body("Service temporarily unavailable", "text/plain")));

        Assert.Equal(HttpStatusCode.OK, error.StatusCode);
        Assert.Equal("Service temporarily unavailable", error.ErrorContent);
    }

    [Fact]
    public async Task Stream_EmptyBody_HasNoErrorContent()
    {
        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(string.Empty)));

        Assert.Null(error.ErrorContent);
    }

    [Fact]
    public async Task Stream_ByteOrderMarkSplitAcrossReads_StillRecognizesAJsonError()
    {
        byte[] body = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(ArrayError)];

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Stream(new TrickleStream(body), "application/json")));

        AssertRetryableQuotaError(error);
    }

    [Fact]
    public async Task Stream_ByteOrderMarkSplitAcrossReads_KeepsTheFirstEvent()
    {
        byte[] body = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes($"data: {Chunk}\n\n")];

        Assert.Equal(["hi"], await StreamTextsAsync(Stream(new TrickleStream(body))));
    }

    [Fact]
    public async Task Stream_GatewayAnsweringWithAnHtmlPage_KeepsTheBodyForDiagnosis()
    {
        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body("<html>maintenance</html>", "text/html")));

        Assert.Equal("<html>maintenance</html>", error.ErrorContent);
    }

    [Fact]
    public async Task Stream_MalformedEvent_KeepsTheJsonExceptionAndTheBody()
    {
        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body("data: {broken\n\n")));

        Assert.IsAssignableFrom<System.Text.Json.JsonException>(error.InnerException);
        Assert.Contains("broken", error.ErrorContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_ThatStallsAfterAChunk_ThrowsTimeoutException()
    {
        var body = new StallingStream(Encoding.UTF8.GetBytes($"data: {Chunk}\n\n"));

        await Assert.ThrowsAsync<TimeoutException>(() => StreamTextsAsync(Stream(body), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Stream_SlowConsumer_IsNotAnIdleServer()
    {
        using var client = CreateClient(Body($"data: {Chunk}\n\ndata: {Chunk}\n\n"), TimeSpan.FromMilliseconds(100));
        var received = 0;

        await foreach (var _ in client.StreamGenerateContentAsync(Request()))
        {
            received++;
            await Task.Delay(250);
        }

        Assert.Equal(2, received);
    }

    [Theory]
    [InlineData("data: {0}\n\n", "data: {0}\n\n")]
    [InlineData("[{0}", ",{0}]")]
    public async Task Stream_SlowConsumer_IsNotAnIdleServerWhenTheNextChunkIsStillToBeRead(string first, string second)
    {
        var body = new CancelableChunksStream(1, string.Format(first, Chunk), string.Format(second, Chunk));
        using var client = CreateClient(Stream(body), TimeSpan.FromMilliseconds(100));
        var received = 0;

        await foreach (var _ in client.StreamGenerateContentAsync(Request()))
        {
            received++;
            await Task.Delay(250);
        }

        Assert.Equal(2, received);
    }

    [Theory]
    [InlineData("[", "]")]
    [InlineData("", "")]
    public async Task Stream_JsonBodyArrivingSlowlyInPieces_IsNotAStall(string prefix, string suffix)
    {
        var body = prefix + Chunk + suffix;
        var pieces = body.Chunk(body.Length / 5 + 1).Select(piece => new string(piece)).ToArray();

        Assert.Equal(["hi"], await StreamTextsAsync(Stream(new CancelableChunksStream(60, pieces), "application/json"), TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task Stream_InfiniteIdleTimeout_WaitsForTheNextChunk()
    {
        var body = new CancelableChunksStream(50, $"data: {Chunk}\n\n", $"data: {Chunk}\n\n");

        Assert.Equal(["hi", "hi"], await StreamTextsAsync(Stream(body), Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task Stream_KeepAliveComments_KeepASlowStreamAlive()
    {
        string[] pieces = [$"data: {Chunk}\n\n", .. Enumerable.Repeat(": ping\n\n", 8), $"data: {Chunk}\n\n"];

        Assert.Equal(["hi", "hi"], await StreamTextsAsync(Stream(new CancelableChunksStream(50, pieces)), TimeSpan.FromMilliseconds(200)));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    public async Task Stream_JsonArray_YieldsEveryElement(string mediaType)
    {
        Assert.Equal(["hi", "hi"], await StreamTextsAsync(Body($"[{Chunk},\r\n{Chunk}]\n", mediaType)));
    }

    [Fact]
    public async Task Stream_ThatStallsBeforeTheFirstByte_ThrowsTimeoutException()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => StreamTextsAsync(Stream(new StallingStream([])), TimeSpan.FromMilliseconds(100)));
    }

    [Theory]
    [InlineData("""{"error":"quota exceeded"}""")]
    [InlineData("""{"error":["quota exceeded"]}""")]
    public async Task Generate_ErrorOfAnyShapeOnA2xx_IsAFailure(string body)
    {
        using var client = CreateClient(Body(body, "application/json"));

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(Request()));

        Assert.Contains("quota exceeded", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    [Fact]
    public async Task Generate_NullError_IsNotAFailure()
    {
        using var client = CreateClient(Body("""{"error":null,"candidates":[]}""", "application/json"));

        Assert.Empty((await client.GenerateContentAsync(Request())).Candidates);
    }

    [Fact]
    public async Task Stream_WithoutServerSentEvents_SendsNoAltSseAndReadsTheArray()
    {
        using var handler = new RecordingHandler($"[{Chunk},{Chunk}]");
        var options = new GeminiClientOptions { ApiKey = "k", Model = "m", UseServerSentEvents = false };
        using var client = new GeminiClient(options, new HttpClient(handler));
        var received = 0;

        await foreach (var _ in client.StreamGenerateContentAsync(Request()))
        {
            received++;
        }

        Assert.Equal(2, received);
        Assert.Empty(handler.LastRequest!.RequestUri!.Query);
        Assert.Empty(handler.LastRequest.Headers.Accept);
    }

    [Fact]
    public async Task Stream_ByDefault_AsksForServerSentEvents()
    {
        using var handler = new RecordingHandler($"data: {Chunk}\n\n", contentType: "text/event-stream");
        using var client = new GeminiClient(new GeminiClientOptions { ApiKey = "k", Model = "m" }, new HttpClient(handler));

        await foreach (var _ in client.StreamGenerateContentAsync(Request()))
        {
        }

        Assert.Equal("?alt=sse", handler.LastRequest!.RequestUri!.Query);
        Assert.Equal("text/event-stream", handler.LastRequest.Headers.Accept.ToString());
    }

    [Fact]
    public async Task Stream_JsonArrayAfterAByteOrderMark_IsRead()
    {
        Assert.Equal(["hi"], await StreamTextsAsync(Body($"\uFEFF[{Chunk}]", "application/json")));
    }

    [Fact]
    public async Task Stream_JsonArraySplitAcrossReads_IsRead()
    {
        Assert.Equal(["hi", "hi"], await StreamTextsAsync(Stream(new TrickleStream(Encoding.UTF8.GetBytes($"[{Chunk},{Chunk}]")), "application/json")));
    }

    [Fact]
    public async Task Stream_JsonArrayThatStalls_ThrowsTimeoutExceptionAfterTheFirstChunk()
    {
        var texts = new List<string?>();
        using var client = CreateClient(Stream(new StallingStream(Encoding.UTF8.GetBytes($"[{Chunk}")), "application/json"), TimeSpan.FromMilliseconds(200));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var chunk in client.StreamGenerateContentAsync(Request(), cancellationToken: guard.Token))
            {
                texts.Add(chunk.Text);
            }
        });

        Assert.Equal(["hi"], texts);
    }

    [Fact]
    public async Task Stream_JsonArray_SurfacesAnErrorElementAfterEarlierChunks()
    {
        const string error = """{"error":{"code":503,"message":"m","status":"UNAVAILABLE"}}""";
        var texts = new List<string?>();
        using var client = CreateClient(Body($"[{Chunk},{error}]", "application/json"));

        var thrown = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var chunk in client.StreamGenerateContentAsync(Request()))
            {
                texts.Add(chunk.Text);
            }
        });

        Assert.Equal(["hi"], texts);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, thrown.StatusCode);
        Assert.Equal("UNAVAILABLE", thrown.ErrorStatus);
        Assert.DoesNotContain("candidates", thrown.ErrorContent, StringComparison.Ordinal);
        Assert.Contains("UNAVAILABLE", thrown.ErrorContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"candidates":""")]
    [InlineData("[1]")]
    public async Task Stream_JsonArrayThatIsMalformed_KeepsTheJsonExceptionAndTheBody(string body)
    {
        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body, "application/json")));

        Assert.IsAssignableFrom<System.Text.Json.JsonException>(error.InnerException);
        Assert.Equal(body, error.ErrorContent);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[null]")]
    public async Task Stream_JsonArrayWithoutChunks_IsReportedAsEmpty(string body)
    {
        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body, "application/json")));

        Assert.Contains("empty streaming response", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_ErrorEventInAGatewayShape_IsAFailure()
    {
        const string data = """{"message":"upstream rate limited"}""";

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body($"data: {Chunk}\n\nevent: error\ndata: {data}\n\n")));

        Assert.Equal(data, error.ErrorContent);
    }

    [Fact]
    public async Task Generate_ErrorStatus_KeepsTheStatusWhateverTheStreamIdleTimeout()
    {
        using var handler = new RecordingHandler("""{"error":{"code":503,"message":"m"}}""", HttpStatusCode.ServiceUnavailable);
        var options = new GeminiClientOptions { Model = "m", StreamIdleTimeout = TimeSpan.MaxValue };
        using var client = new GeminiClient(options, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(Request()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public async Task Stream_ErrorEventInGooglesShape_KeepsItsDetails()
    {
        const string data = """{"error":{"code":503,"message":"m","status":"UNAVAILABLE"}}""";

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body($"event: error\ndata: {data}\n\n")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Fact]
    public async Task Stream_JsonArray_StopsWhenTheCallerCancels()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var client = CreateClient(Stream(new StallingStream(Encoding.UTF8.GetBytes($"[{Chunk}")), "application/json"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamGenerateContentAsync(Request(), cancellationToken: cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Stream_CallerCancellation_IsNotReportedAsATimeout()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var body = new StallingStream(Encoding.UTF8.GetBytes($"data: {Chunk}\n\n"));
        using var client = CreateClient(Stream(body));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamGenerateContentAsync(Request(), cancellationToken: cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Stream_LostConnection_ThrowsHttpRequestExceptionWithTheStatus()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => StreamTextsAsync(Stream(new FailingStream())));

        Assert.Equal(HttpStatusCode.OK, error.StatusCode);
        Assert.IsType<IOException>(error.InnerException);
    }

    [Fact]
    public async Task Generate_Http200ErrorBody_ThrowsInsteadOfReturningAnEmptyResponse()
    {
        const string body = """{"error":{"code":500,"message":"gateway failed","status":"INTERNAL"}}""";
        using var client = CreateClient(Body(body, "application/json"));

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(Request()));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.Equal("gateway failed", error.ErrorMessage);
    }

    [Fact]
    public async Task Generate_ErrorStatus_ReadsRetryDelayFromTheBody()
    {
        const string body = """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"m","details":[{"retryDelay":"1.5s"}]}}""";
        using var client = CreateClient(Body(body, "application/json", HttpStatusCode.TooManyRequests));

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(Request()));

        Assert.Equal(TimeSpan.FromSeconds(1.5), error.RetryAfter);
    }

    [Fact]
    public async Task Generate_ErrorStatus_FallsBackToRetryAfterHeaderAndTruncatesTheBody()
    {
        var handler = new StubHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(new string('x', 200_000)) };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(Request()));

        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
        Assert.Equal(64 * 1024, error.ErrorContent?.Length);
    }

    [Fact]
    public async Task Stream_ErrorStatusWithAStalledBody_StillReportsTheStatus()
    {
        var error = await Assert.ThrowsAsync<GeminiApiException>(
            () => StreamTextsAsync(Stream(new StallingStream([]), "application/json", HttpStatusCode.BadGateway), TimeSpan.FromMilliseconds(100)));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
    }

    [Fact]
    public async Task Stream_ErrorStatusWithAnEndlessBody_StopsReadingAndKeepsTheStatus()
    {
        var body = new EndlessStream();

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Stream(body, "application/json", HttpStatusCode.BadGateway)));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Null(error.ErrorContent);
        Assert.InRange(body.BytesRead, 64 * 1024, 1024 * 1024);
    }

    private sealed class EndlessStream : MemoryStream
    {
        public long BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'x');
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    /// <summary>
    /// Returns each text in a read of its own.
    /// </summary>
    private sealed class ChunkedStream(params string[] chunks) : MemoryStream
    {
        private int _next;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next == chunks.Length)
            {
                return ValueTask.FromResult(0);
            }

            var bytes = Encoding.UTF8.GetBytes(chunks[_next++]);
            bytes.CopyTo(buffer);
            return ValueTask.FromResult(bytes.Length);
        }
    }

    /// <summary>
    /// Returns one byte per read.
    /// </summary>
    private sealed class TrickleStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    /// <summary>
    /// Returns its bytes, then waits until the read is canceled.
    /// </summary>
    private sealed class StallingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// Returns one chunk per read after a delay, and honors cancellation like a real connection.
    /// </summary>
    private sealed class CancelableChunksStream(int delayMilliseconds, params string[] chunks) : MemoryStream
    {
        private int _next;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delayMilliseconds, cancellationToken);
            if (_next == chunks.Length)
            {
                return 0;
            }

            var bytes = Encoding.UTF8.GetBytes(chunks[_next++]);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection lost"));
    }
}
