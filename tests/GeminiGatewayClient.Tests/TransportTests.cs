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
        var texts = new List<string?>();
        await foreach (var chunk in client.StreamGenerateContentAsync(Request()))
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

    [Fact]
    public async Task ErrorEvent_WithAPermanentStatus_IsNotTransient()
    {
        const string body = """data: {"error":{"code":3,"status":"INVALID_ARGUMENT","message":"m"}}""";

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body + "\n\n")));

        Assert.False(error.IsTransient);
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
    public async Task BatchEmbed_MixingModels_IsRejected()
    {
        using var client = CreateClient(Body("{}", "application/json"));
        var request = new BatchEmbedContentsRequest
        {
            Requests =
            [
                new EmbedContentRequest { Model = "a", Content = Content.FromText("x") },
                new EmbedContentRequest { Model = "b", Content = Content.FromText("y") }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.BatchEmbedContentsAsync(request));
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
    public async Task Stream_WithoutAnyEvent_IsReportedAsEmpty(string body)
    {
        await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body)));
    }

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
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Stream_GatewayAnsweringWithAJsonError_SurfacesTheError()
    {
        const string body = """{"error":{"code":429,"message":"slow down","status":"RESOURCE_EXHAUSTED"}}""";

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => StreamTextsAsync(Body(body, "application/json")));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal("slow down", error.ErrorMessage);
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

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection lost"));
    }
}
