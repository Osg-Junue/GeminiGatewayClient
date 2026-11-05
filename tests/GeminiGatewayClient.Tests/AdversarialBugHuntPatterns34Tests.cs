using System.Net;
using System.Text;
using GeminiGatewayClient;
using GeminiGatewayClient.Http;
using GeminiGatewayClient.Models;
using Xunit;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// Adversarial unit tests targeting Bug Patterns 3 and 4:
/// - Pattern 3: Fast-path / slow-path state asymmetry (UTF-8 BOM handling, comment prefixes, HTTP 200 JSON errors, split CR at buffer boundaries).
/// - Pattern 4: Stream chunking &amp; size limits (1-byte chunk delivery, Japanese/Emoji multi-byte character splitting, 1-byte SSE line delimiters, size limit bounds).
/// </summary>
public sealed class AdversarialBugHuntPatterns34Tests
{
    private static readonly UTF8Encoding s_utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    #region Pattern 3: Fast-Path / Slow-Path State Asymmetry

    [Fact]
    public async Task Pattern3_Sse_Utf8BomOnComment_FollowedByMultilineEvent_ParsedCorrectly()
    {
        // UTF-8 BOM on an SSE comment line (\uFEFF:comment\r\n\r\n) followed immediately by a multi-line SSE event
        var sseText = "\uFEFF:comment\r\n\r\n" +
                      "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r\n" +
                      "data: \"parts\":[{\"text\":\"multiline-after-bom-comment\"}]}}]}\r\n\r\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseText));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.NotNull(response.Candidates);
        var candidate = Assert.Single(response.Candidates);
        Assert.Equal(Role.Model, candidate.Content?.Role);
        Assert.Equal("multiline-after-bom-comment", response.Text);
    }

    [Fact]
    public async Task Pattern3_Sse_Utf8BomOnCommentWithoutBlankLine_FollowedByMultilineEvent_ParsedCorrectly()
    {
        // Comment directly followed by data: without an intermediate blank line
        var sseText = "\uFEFF:comment\r\n" +
                      "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r\n" +
                      "data: \"parts\":[{\"text\":\"direct-after-comment\"}]}}]}\r\n\r\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseText));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal("direct-after-comment", response.Text);
    }

    [Fact]
    public async Task Pattern3_Sse_Utf8BomOnMultipleComments_FollowedByMultilineEvent_ParsedCorrectly()
    {
        var sseText = "\uFEFF:keepalive 1\r\n:keepalive 2\r\n\r\n" +
                      "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\n" +
                      "data: \"parts\":[{\"text\":\"after-multiple-comments\"}]}}]}\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseText));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal("after-multiple-comments", response.Text);
    }

    [Fact]
    public async Task Pattern3_Sse_Utf8BomOnHttp200NonSseJsonError_ThrowsGeminiApiException()
    {
        // HTTP 200 response with UTF-8 BOM followed by raw non-SSE JSON error payload without newlines
        var errorJson = "\uFEFF{\"error\":{\"code\":401,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"UNAUTHENTICATED\"}}";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(errorJson));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                stream,
                GeminiStreamingFormat.ServerSentEvents,
                HttpStatusCode.OK,
                watchdog))
            {
            }
        });

        // 401 UNAUTHENTICATED is mapped to HttpStatusCode.Unauthorized
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(401, ex.ErrorCode);
        Assert.Equal("UNAUTHENTICATED", ex.ErrorStatus);
        Assert.Contains("API key not valid", ex.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pattern3_Sse_Utf8BomOnHttp200NonSseJsonError_WithTrailingNewlines_ThrowsGeminiApiException()
    {
        var errorJson = "\uFEFF{\"error\":{\"code\":403,\"message\":\"Permission denied for resource.\",\"status\":\"PERMISSION_DENIED\"}}\r\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(errorJson));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                stream,
                GeminiStreamingFormat.ServerSentEvents,
                HttpStatusCode.OK,
                watchdog))
            {
            }
        });

        // 403 PERMISSION_DENIED is mapped to HttpStatusCode.Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal(403, ex.ErrorCode);
        Assert.Equal("PERMISSION_DENIED", ex.ErrorStatus);
        Assert.Contains("Permission denied", ex.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pattern3_Sse_Utf8BomOnHttp200NonSseJsonError_PrettyPrinted_ThrowsGeminiApiException()
    {
        // Pretty-printed multi-line non-SSE JSON error starting with UTF-8 BOM
        var errorJson = "\uFEFF{\r\n" +
                        "  \"error\": {\r\n" +
                        "    \"code\": 429,\r\n" +
                        "    \"message\": \"Resource has been exhausted (e.g. check quota).\",\r\n" +
                        "    \"status\": \"RESOURCE_EXHAUSTED\"\r\n" +
                        "  }\r\n" +
                        "}";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(errorJson));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                stream,
                GeminiStreamingFormat.ServerSentEvents,
                HttpStatusCode.OK,
                watchdog))
            {
            }
        });

        // 429 RESOURCE_EXHAUSTED is mapped to HttpStatusCode.TooManyRequests
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(429, ex.ErrorCode);
        Assert.Equal("RESOURCE_EXHAUSTED", ex.ErrorStatus);
        Assert.Contains("Resource has been exhausted", ex.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pattern3_JsonArray_Utf8BomOnHttp200ArrayContainingError_ThrowsGeminiApiException()
    {
        var errorJson = "\uFEFF[{\"error\":{\"code\":503,\"message\":\"Service temporarily unavailable.\",\"status\":\"UNAVAILABLE\"}}]";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(errorJson));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                stream,
                GeminiStreamingFormat.JsonArray,
                HttpStatusCode.OK,
                watchdog))
            {
            }
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(503, ex.ErrorCode);
        Assert.Equal("UNAVAILABLE", ex.ErrorStatus);
        Assert.Contains("Service temporarily unavailable", ex.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pattern3_JsonArray_Utf8BomOnHttp200NonArrayJsonError_ThrowsGeminiApiException()
    {
        var errorJson = "\uFEFF{\"error\":{\"code\":400,\"message\":\"Invalid argument in request.\",\"status\":\"INVALID_ARGUMENT\"}}";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(errorJson));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                stream,
                GeminiStreamingFormat.JsonArray,
                HttpStatusCode.OK,
                watchdog))
            {
            }
        });

        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Pattern3_Sse_SplitCrAtBufferBoundary_FastPathCandidateTransitionsToMultilineSlowPath()
    {
        // Chunk 1 ends with '\r' on line 1 of an event that looks like a single-line candidate,
        // but chunk 2 begins with '\ndata:' which continues the multi-line slow path.
        var chunk1 = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r";
        var chunk2 = "\ndata: \"parts\":[{\"text\":\"split-cr-fast-to-slow\"}]}}]}\r\n\r\n";

        await using var stream = new ExplicitChunkStream(
            Encoding.UTF8.GetBytes(chunk1),
            Encoding.UTF8.GetBytes(chunk2));

        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal("split-cr-fast-to-slow", response.Text);
    }

    [Fact]
    public async Task Pattern3_Sse_SplitCrAtBufferBoundary_BetweenFastPathEventAndSubsequentMultilineEvent()
    {
        // Event 1 is a single-line fast-path event whose terminating blank line has '\r' at chunk 1 boundary and '\n' in chunk 2.
        // Chunk 2 immediately starts Event 2, which is a multi-line slow-path event.
        var chunk1 = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"fast-event-1\"}]}}]}\r\n\r";
        var chunk2 = "\ndata: {\"candidates\":[{\"content\":{\"role\":\"model\",\r\ndata: \"parts\":[{\"text\":\"slow-multiline-2\"}]}}]}\r\n\r\n";

        await using var stream = new ExplicitChunkStream(
            Encoding.UTF8.GetBytes(chunk1),
            Encoding.UTF8.GetBytes(chunk2));

        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        Assert.Equal(2, responses.Count);
        Assert.Equal("fast-event-1", responses[0].Text);
        Assert.Equal("slow-multiline-2", responses[1].Text);
    }

    [Fact]
    public async Task Pattern3_Sse_SplitCrAtBufferBoundary_WithinMultilineSlowPathBetweenLines()
    {
        // Multi-line slow-path event with multiple data lines where '\r' is split across chunk boundaries between valid JSON object tokens
        var chunk1 = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r";
        var chunk2 = "\ndata: \"parts\":[{\"text\":\"segment-A\"},";
        var chunk3 = "\r";
        var chunk4 = "\ndata: {\"text\":\"segment-B\"}]}}]}\r\n\r\n";

        await using var stream = new ExplicitChunkStream(
            Encoding.UTF8.GetBytes(chunk1),
            Encoding.UTF8.GetBytes(chunk2),
            Encoding.UTF8.GetBytes(chunk3),
            Encoding.UTF8.GetBytes(chunk4));

        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal("segment-Asegment-B", response.Text);
    }

    [Fact]
    public async Task Pattern3_Sse_SplitCrAtBufferBoundary_FastPathEventFollowedByBOMAndMultilineEvent()
    {
        // Event 1 fast path ends with \r at chunk boundary; chunk 2 delivers \n, then comment with BOM, then multi-line event
        var chunk1 = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"first-event\"}]}}]}\r\n\r";
        var chunk2 = "\n\uFEFF:intermediate-comment\r\n\r\ndata: {\"candidates\":[{\"content\":{\"role\":\"model\",\r\ndata: \"parts\":[{\"text\":\"second-event\"}]}}]}\r\n\r\n";

        await using var stream = new ExplicitChunkStream(
            Encoding.UTF8.GetBytes(chunk1),
            Encoding.UTF8.GetBytes(chunk2));

        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        Assert.Equal(2, responses.Count);
        Assert.Equal("first-event", responses[0].Text);
        Assert.Equal("second-event", responses[1].Text);
    }

    #endregion

    #region Pattern 4: Stream Chunking & Size Limits

    [Fact]
    public async Task Pattern4_JsonArray_OneByteChunkDelivery_SingleElement_ParsedSuccessfully()
    {
        var json = "[{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Single 1-byte chunk delivery\"}]}}]}]";

        await using var stream = new OneByteStream(json);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.JsonArray,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal("Single 1-byte chunk delivery", response.Text);
    }

    [Fact]
    public async Task Pattern4_JsonArray_OneByteChunkDelivery_MultipleElements_AllParsedInOrder()
    {
        var json = "[" +
                   "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"alpha\"}]}}]}," +
                   "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"beta\"}]}}]}," +
                   "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"gamma\"}]}}]}" +
                   "]";

        await using var stream = new OneByteStream(json);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<string?>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.JsonArray,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp.Text);
        }

        Assert.Equal(["alpha", "beta", "gamma"], responses);
    }

    [Fact]
    public async Task Pattern4_JsonArray_OneByteChunkDelivery_WithWhitespaceAndNewlines()
    {
        var json = "[\r\n" +
                   "  {\r\n" +
                   "    \"candidates\": [\r\n" +
                   "      {\r\n" +
                   "        \"content\": {\r\n" +
                   "          \"parts\": [{\"text\": \"Indented item\"}]\r\n" +
                   "        }\r\n" +
                   "      }\r\n" +
                   "    ]\r\n" +
                   "  }\r\n" +
                   "]";

        await using var stream = new OneByteStream(json);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.JsonArray,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal("Indented item", response.Text);
    }

    [Fact]
    public async Task Pattern4_JsonArray_JapaneseCharacters_SplitAcrossOneByteChunks()
    {
        // Japanese 3-byte UTF-8 sequences delivered 1 byte at a time
        const string expectedText1 = "日本語テスト：ひらがな、カタカナ、漢字、句読点。";
        const string expectedText2 = "東京都千代田区永田町 🌸 富士山";
        var json = $"[{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":\"{expectedText1}\"}}]}}}}]}}," +
                   $"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":\"{expectedText2}\"}}]}}}}]}}]";

        await using var stream = new OneByteStream(json);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<string?>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.JsonArray,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp.Text);
        }

        Assert.Equal([expectedText1, expectedText2], responses);
    }

    [Fact]
    public async Task Pattern4_JsonArray_ComplexEmojiAndSurrogatePairs_SplitAcrossOneByteChunks()
    {
        // 4-byte astral characters, ZWJ sequences, skin-tone modifiers, flags, and keycaps split across 1-byte chunks
        const string complexEmojiText = "Astral: 🌟 🚀 🐱‍👤 | ZWJ: 👩🏽‍💻 | Flag: 🇯🇵 | Keycap: #️⃣";
        var json = $"[{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":\"{complexEmojiText}\"}}]}}}}]}}]";

        await using var stream = new OneByteStream(json);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.JsonArray,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal(complexEmojiText, response.Text);
    }

    [Fact]
    public async Task Pattern4_Sse_MultilineEvent_LfDelimiters_OneByteChunkDelivery()
    {
        // Multi-line event with \n delimiters delivered 1 byte at a time
        var sse = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\n" +
                  "data: \"parts\":[{\"text\":\"multiline-lf-1byte\"}]}}]}\n\n";

        await using var stream = new OneByteStream(sse);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal("multiline-lf-1byte", response.Text);
    }

    [Fact]
    public async Task Pattern4_Sse_MultilineEvent_CrlfDelimiters_OneByteChunkDelivery()
    {
        // Multi-line event with \r\n delimiters delivered 1 byte at a time
        var sse = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r\n" +
                  "data: \"parts\":[{\"text\":\"multiline-crlf-1byte\"}]}}]}\r\n\r\n";

        await using var stream = new OneByteStream(sse);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal("multiline-crlf-1byte", response.Text);
    }

    [Fact]
    public async Task Pattern4_Sse_MultilineEvent_MixedDelimiters_OneByteChunkDelivery()
    {
        // Multi-line event with mixed delimiters: line 1 \r\n, line 2 \n, line 3 \r, line 4 terminator \r\n
        var sse = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r\n" +
                  "data: \"parts\":[{\"text\":\"mixed-\"}," +
                  "\n" +
                  "data: {\"text\":\"delimiters-\"}," +
                  "\r" +
                  "data: {\"text\":\"ok\"}]}}]}\r\n\r\n";

        await using var stream = new OneByteStream(sse);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal("mixed-delimiters-ok", response.Text);
    }

    [Fact]
    public async Task Pattern4_Sse_MultilineEvent_JapaneseAndEmoji_OneByteChunkDelivery()
    {
        const string japaneseAndEmoji = "マルチライン日本語：こんにちは 🌟 富士山 🇯🇵";
        var sse = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r\n" +
                  $"data: \"parts\":[{{\"text\":\"{japaneseAndEmoji}\"}}]}}}}]}}\r\n\r\n";

        await using var stream = new OneByteStream(sse);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp);
        }

        var response = Assert.Single(responses);
        Assert.Equal(japaneseAndEmoji, response.Text);
    }

    [Fact]
    public async Task Pattern4_Sse_MultipleConsecutiveMultilineEvents_OneByteChunkDelivery()
    {
        var sse = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r\n" +
                  "data: \"parts\":[{\"text\":\"event-one\"}]}}]}\r\n\r\n" +
                  ":comment-between\r\n\r\n" +
                  "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\n" +
                  "data: \"parts\":[{\"text\":\"event-two\"}]}}]}\n\n";

        await using var stream = new OneByteStream(sse);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<string?>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(
            stream,
            GeminiStreamingFormat.ServerSentEvents,
            HttpStatusCode.OK,
            watchdog))
        {
            responses.Add(resp.Text);
        }

        Assert.Equal(["event-one", "event-two"], responses);
    }

    [Fact]
    public async Task Pattern4_Sse_SizeLimitEnforcement_OneByteChunkDelivery_ThrowsWhenExceedingLimit()
    {
        // Combined payload of multi-line event exceeds maxSseEventBytes
        const int limit = 80;
        var sse = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r\n" +
                  "data: \"parts\":[{\"text\":\"" + new string('a', 100) + "\"}]}}]}\r\n\r\n";

        await using var stream = new OneByteStream(sse);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                stream,
                GeminiStreamingFormat.ServerSentEvents,
                HttpStatusCode.OK,
                watchdog,
                maxSseEventBytes: limit))
            {
            }
        });

        Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pattern4_Sse_SizeLimitEnforcement_ExactLimitPasses_LimitMinusOneFails()
    {
        // Verify boundary behavior: line.Length includes "data: " prefix.
        var baseJson = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"x\"}]}}]}";
        var lineBytes = s_utf8WithoutBom.GetBytes($"data: {baseJson}");
        var exactLimit = lineBytes.Length;

        var passingSse = $"data: {baseJson}\r\n\r\n";

        // 1. Exact limit: line.Length == exactLimit -> passes
        await using (var passStream = new OneByteStream(passingSse))
        using (var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None))
        {
            var responses = new List<GenerateContentResponse>();
            await foreach (var resp in GeminiStreamReader.ReadAsync(
                passStream,
                GeminiStreamingFormat.ServerSentEvents,
                HttpStatusCode.OK,
                watchdog,
                maxSseEventBytes: exactLimit))
            {
                responses.Add(resp);
            }

            Assert.Single(responses);
            Assert.Equal("x", responses[0].Text);
        }

        // 2. Limit minus one (exactLimit - 1): line.Length > (exactLimit - 1) -> throws
        await using (var failStream = new OneByteStream(passingSse))
        using (var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None))
        {
            var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
            {
                await foreach (var _ in GeminiStreamReader.ReadAsync(
                    failStream,
                    GeminiStreamingFormat.ServerSentEvents,
                    HttpStatusCode.OK,
                    watchdog,
                    maxSseEventBytes: exactLimit - 1))
                {
                }
            });

            Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Pattern4_JsonArray_SizeLimitEnforcement_OneByteChunkDelivery_ThrowsWhenExceedingLimit()
    {
        const int limit = 70;
        var json = "[{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + new string('y', 150) + "\"}]}}]}]";

        await using var stream = new OneByteStream(json);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(
                stream,
                GeminiStreamingFormat.JsonArray,
                HttpStatusCode.OK,
                watchdog,
                maxSseEventBytes: limit))
            {
            }
        });

        Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pattern4_JsonArray_SizeLimitEnforcement_ExactLimitPasses_LimitMinusOneFails()
    {
        // For JsonArray streaming, PrefixRecordingStream tracks _currentElementBytes and throws if > maxElementBytes.
        var elementJson = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"z\"}]}}]}";
        var fullArray = $"[{elementJson}]";
        var fullBytes = s_utf8WithoutBom.GetBytes(fullArray).Length;

        // 1. With maxElementBytes >= fullBytes, the 1-byte chunk stream successfully reads and yields the item
        await using (var passStream = new OneByteStream(fullArray))
        using (var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None))
        {
            var responses = new List<GenerateContentResponse>();
            await foreach (var resp in GeminiStreamReader.ReadAsync(
                passStream,
                GeminiStreamingFormat.JsonArray,
                HttpStatusCode.OK,
                watchdog,
                maxSseEventBytes: fullBytes))
            {
                responses.Add(resp);
            }

            Assert.Single(responses);
            Assert.Equal("z", responses[0].Text);
        }

        // 2. Limit minus one throws because bytes accumulated before element yield exceed limit
        await using (var failStream = new OneByteStream(fullArray))
        using (var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None))
        {
            var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
            {
                await foreach (var _ in GeminiStreamReader.ReadAsync(
                    failStream,
                    GeminiStreamingFormat.JsonArray,
                    HttpStatusCode.OK,
                    watchdog,
                    maxSseEventBytes: fullBytes - 1))
                {
                }
            });

            Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region Helper Stream Classes

    /// <summary>
    /// Stream that returns at most 1 byte per ReadAsync invocation, simulating extreme network packet fragmentation.
    /// </summary>
    private sealed class OneByteStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _data;
        private int _position;

        public OneByteStream(string text) : this(Encoding.UTF8.GetBytes(text)) { }

        public OneByteStream(byte[] data)
        {
            _data = data;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous reads are not supported.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            if (buffer.Length == 0 || _position >= _data.Length)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = _data.Span[_position++];
            return ValueTask.FromResult(1);
        }
    }

    /// <summary>
    /// Stream that returns pre-split chunks in discrete ReadAsync invocations to test exact buffer boundaries.
    /// </summary>
    private sealed class ExplicitChunkStream(params byte[][] chunks) : Stream
    {
        private readonly IReadOnlyList<byte[]> _chunks = chunks;
        private int _chunkIdx;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous reads are not supported.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            if (_chunkIdx >= _chunks.Count)
            {
                return ValueTask.FromResult(0);
            }

            var chunk = _chunks[_chunkIdx++];
            var count = Math.Min(buffer.Length, chunk.Length);
            chunk.AsMemory(0, count).CopyTo(buffer);
            return ValueTask.FromResult(count);
        }
    }

    #endregion
}
