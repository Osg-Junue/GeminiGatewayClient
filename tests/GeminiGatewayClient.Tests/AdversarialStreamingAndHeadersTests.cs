using System.Net;
using System.Text;
using GeminiGatewayClient;
using GeminiGatewayClient.Http;
using GeminiGatewayClient.Models;
using Xunit;

namespace GeminiGatewayClient.Tests;

public sealed class AdversarialStreamingAndHeadersTests
{
    private static readonly string s_sampleJson = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"sample text\"}]}}]}";

    #region GeminiStreamReader: Chunk Boundaries and Split CRLF

    [Fact]
    public async Task GeminiStreamReader_SplitCrlf_BetweenLineAndBlankLine_ParsedCorrectly()
    {
        // Chunk 1 ends with '\r', chunk 2 starts with '\n'
        var line = $"data: {s_sampleJson}\r";
        var blank = "\n\r\n";

        await using var stream = new ChunkStream(
            Encoding.UTF8.GetBytes(line),
            Encoding.UTF8.GetBytes(blank));

        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp);
        }

        Assert.Single(responses);
        Assert.Equal("sample text", responses[0].Text);
    }

    [Fact]
    public async Task GeminiStreamReader_SplitCrlf_InsideBlankLineTerminator_ParsedCorrectly()
    {
        // Line ends with \r\n, then blank line has \r in chunk 1 and \n in chunk 2
        var chunk1 = $"data: {s_sampleJson}\r\n\r";
        var chunk2 = "\n";

        await using var stream = new ChunkStream(
            Encoding.UTF8.GetBytes(chunk1),
            Encoding.UTF8.GetBytes(chunk2));

        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp);
        }

        Assert.Single(responses);
        Assert.Equal("sample text", responses[0].Text);
    }

    [Fact]
    public async Task GeminiStreamReader_SplitCrlf_AcrossMultipleEventsAndEof_ParsedCorrectly()
    {
        var e1Chunk1 = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"one\"}]}}]}\r";
        var e1Chunk2 = "\n\r";
        var e1Chunk3 = "\n";
        var e2Chunk1 = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"two\"}]}}]}\r\n";
        var e2Chunk2 = "\r\n";

        await using var stream = new ChunkStream(
            Encoding.UTF8.GetBytes(e1Chunk1),
            Encoding.UTF8.GetBytes(e1Chunk2),
            Encoding.UTF8.GetBytes(e1Chunk3),
            Encoding.UTF8.GetBytes(e2Chunk1),
            Encoding.UTF8.GetBytes(e2Chunk2));

        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var responses = new List<string?>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp.Text);
        }

        Assert.Equal(["one", "two"], responses);
    }

    [Fact]
    public async Task GeminiStreamReader_SplitCrlf_InMultilineEvent_ParsedCorrectly()
    {
        // Multi-line event split across CRLF boundary between data lines
        var chunk1 = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\r";
        var chunk2 = "\ndata: \"parts\":[{\"text\":\"multiline-ok\"}]}}]}\r\n\r\n";

        await using var stream = new ChunkStream(
            Encoding.UTF8.GetBytes(chunk1),
            Encoding.UTF8.GetBytes(chunk2));

        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var responses = new List<string?>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp.Text);
        }

        Assert.Single(responses);
        Assert.Equal("multiline-ok", responses[0]);
    }

    #endregion

    #region GeminiStreamReader: Multi-Byte UTF-8 Across Chunk Boundaries

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public async Task GeminiStreamReader_MultiByteUtf8_JapaneseAndEmoji_SurvivesVariousChunkSizes(int chunkSize)
    {
        const string text = "こんにちは世界！🎉🍣🔥 漢字と絵文字テスト";
        var json = $"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":\"{text}\"}}]}}}}]}}";
        var sse = $"data: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(sse);

        await using var stream = new FixedChunkSizeStream(bytes, chunkSize);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<string?>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp.Text);
        }

        Assert.Single(responses);
        Assert.Equal(text, responses[0]);
    }

    [Fact]
    public async Task GeminiStreamReader_MultiByteUtf8_SplitRightInMiddleOfFourByteEmoji()
    {
        // 4-byte emoji: 🎉 is 0xF0, 0x9F, 0x8E, 0x89
        var prefix = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"";
        var suffix = "\"}]}}]}\n\n";

        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var suffixBytes = Encoding.UTF8.GetBytes(suffix);
        byte[] emojiBytes = [0xF0, 0x9F, 0x8E, 0x89];

        // Split 1: prefix + first byte of emoji
        var chunk1 = new byte[prefixBytes.Length + 1];
        prefixBytes.CopyTo(chunk1, 0);
        chunk1[^1] = emojiBytes[0]; // 0xF0

        // Split 2: next 2 bytes of emoji
        byte[] chunk2 = [emojiBytes[1], emojiBytes[2]]; // 0x9F, 0x8E

        // Split 3: last byte of emoji + suffix
        var chunk3 = new byte[1 + suffixBytes.Length];
        chunk3[0] = emojiBytes[3]; // 0x89
        suffixBytes.CopyTo(chunk3, 1);

        await using var stream = new ChunkStream(chunk1, chunk2, chunk3);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<string?>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp.Text);
        }

        Assert.Single(responses);
        Assert.Equal("🎉", responses[0]);
    }

    #endregion

    #region GeminiStreamReader: Strict maxSseEventBytes Enforcement

    [Fact]
    public async Task GeminiStreamReader_MaxSseEventBytes_ExactLimit_Succeeds()
    {
        // Payload has exact length = maxSseEventBytes
        var innerJson = s_sampleJson;
        var sseLine = $"data: {innerJson}"; // Length of "data: {innerJson}"
        var limit = Encoding.UTF8.GetByteCount(sseLine);
        var sse = $"{sseLine}\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog, maxSseEventBytes: limit))
        {
            responses.Add(resp);
        }

        Assert.Single(responses);
        Assert.Equal("sample text", responses[0].Text);
    }

    [Fact]
    public async Task GeminiStreamReader_MaxSseEventBytes_ExceededByOneByte_ThrowsGeminiApiException()
    {
        var innerJson = s_sampleJson;
        var sseLine = $"data: {innerJson}";
        var limit = Encoding.UTF8.GetByteCount(sseLine) - 1; // Limit is 1 byte LESS than line length
        var sse = $"{sseLine}\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog, maxSseEventBytes: limit))
            {
            }
        });

        Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeminiStreamReader_MaxSseEventBytes_ExceededByOneByte_AcrossSlowChunks_ThrowsImmediately()
    {
        const int limit = 30;
        // 31 bytes of 'a' without newline, read in 1-byte chunks
        var bytes = Encoding.UTF8.GetBytes(new string('a', limit + 1));
        await using var stream = new FixedChunkSizeStream(bytes, chunkSize: 1);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog, maxSseEventBytes: limit))
            {
            }
        });

        Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeminiStreamReader_MaxSseEventBytes_MultilinePayloadSum_ExactLimitSucceeds_LimitPlusOneThrows()
    {
        var p1 = "{\"candidates\":[{\"content\":{\"parts\":[";
        var p2 = "{\"text\":\"hi\"}]}}]}";
        // Total payload length = p1.Length + 1 (separator '\n') + p2.Length
        var totalPayloadBytes = Encoding.UTF8.GetByteCount(p1) + 1 + Encoding.UTF8.GetByteCount(p2);

        var sse = $"data: {p1}\ndata: {p2}\n\n";

        // Exactly totalPayloadBytes -> succeeds
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
            using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
            var count = 0;
            await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog, maxSseEventBytes: totalPayloadBytes + 10))
            {
                count++;
                Assert.Equal("hi", resp.Text);
            }
            Assert.Equal(1, count);
        }

        // Limit is totalPayloadBytes - 1 -> throws
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
            using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
            var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
            {
                await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog, maxSseEventBytes: totalPayloadBytes - 1))
                {
                }
            });
            Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GeminiStreamReader_MaxSseEventBytes_EofWithoutNewline_EnforcesLimit()
    {
        const int limit = 20;
        // Exact limit without newline at EOF
        var exactBytes = Encoding.UTF8.GetBytes(new string('a', limit));
        {
            using var stream = new MemoryStream(exactBytes);
            using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
            try
            {
                await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog, maxSseEventBytes: limit))
                {
                }
            }
            catch (GeminiApiException ex) when (!ex.Message.Contains("maximum allowed length", StringComparison.OrdinalIgnoreCase))
            {
                // Non-SSE parse failure or empty stream is expected for raw 'a'*20
            }
        }

        // Limit + 1 without newline at EOF -> MUST throw "exceeded the maximum allowed length"
        var exceedBytes = Encoding.UTF8.GetBytes(new string('a', limit + 1));
        {
            using var stream = new MemoryStream(exceedBytes);
            using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);
            var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
            {
                await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog, maxSseEventBytes: limit))
                {
                }
            });
            Assert.Contains("maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region SearchValues<byte>: Empty Stream, Bare LF, Bare CR, Mixed Newlines

    [Fact]
    public async Task GeminiStreamReader_EmptyStream_ThrowsGeminiApiException()
    {
        using var stream = new MemoryStream([]);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
            {
            }
        });

        Assert.Contains("empty Gemini streaming response body", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeminiStreamReader_BareLf_SingleAndMultiLine_ParsedCorrectly()
    {
        var sse = $"data: {s_sampleJson}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp);
        }

        Assert.Single(responses);
        Assert.Equal("sample text", responses[0].Text);
    }

    [Fact]
    public async Task GeminiStreamReader_BareCr_SingleAndMultiLine_ParsedCorrectly()
    {
        var sse = $"data: {s_sampleJson}\r\r";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp);
        }

        Assert.Single(responses);
        Assert.Equal("sample text", responses[0].Text);
    }

    [Fact]
    public async Task GeminiStreamReader_MixedNewlines_Crlf_Lf_Cr_Interspersed_ParsedCorrectly()
    {
        // Event 1: \r\n line, \n blank line
        // Event 2: \n line, \r\n blank line
        // Event 3: \r line, \r blank line
        // Event 4: \r\n line, \r blank line
        var sse =
            $"data: {s_sampleJson.Replace("sample text", "e1")}\r\n\n" +
            $"data: {s_sampleJson.Replace("sample text", "e2")}\n\r\n" +
            $"data: {s_sampleJson.Replace("sample text", "e3")}\r\r" +
            $"data: {s_sampleJson.Replace("sample text", "e4")}\r\n\r" +
            "data: [DONE]\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var texts = new List<string?>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            texts.Add(resp.Text);
        }

        Assert.Equal(["e1", "e2", "e3", "e4"], texts);
    }

    [Fact]
    public async Task GeminiStreamReader_ConsecutiveBlankLinesAndComments_HandledGracefully()
    {
        var sse =
            "\n\r\n\r\r\n" +
            ":comment 1\n" +
            ":comment 2\r\n" +
            $"data: {s_sampleJson}\n\n" +
            ":comment 3\r" +
            "\n\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp);
        }

        Assert.Single(responses);
        Assert.Equal("sample text", responses[0].Text);
    }

    #endregion

    #region GeminiRequestHeaders: Control Characters 0-31, DEL (127), and Horizontal Tab

    [Fact]
    public void GeminiRequestHeaders_HeaderValue_ControlChars0To31And127_ExhaustiveVerification()
    {
        // Character 9 (\t) MUST be allowed in header values.
        // Characters 0-8, 10-31, and 127 (DEL) MUST be rejected.
        for (var c = 0; c <= 31; c++)
        {
            var ch = (char)c;
            var testValue = $"prefix{ch}suffix";

            if (c == 9) // '\t' (horizontal tab)
            {
                var options = new GeminiClientOptions
                {
                    ApiKey = "test-key",
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["X-Custom-Tab"] = testValue
                    }
                };

                var headers = GeminiRequestHeaders.Create(options);
                using var message = new HttpRequestMessage();
                var vt = headers.ApplyAsync(message, CancellationToken.None);
                Assert.True(vt.IsCompletedSuccessfully);
                Assert.True(message.Headers.TryGetValues("X-Custom-Tab", out var vals));
                Assert.Equal(testValue, Assert.Single(vals));
            }
            else
            {
                var options = new GeminiClientOptions
                {
                    ApiKey = "test-key",
                    AdditionalHeaders = new Dictionary<string, string>
                    {
                        ["X-Custom-Control"] = testValue
                    }
                };

                var ex = Assert.Throws<ArgumentException>(() => GeminiRequestHeaders.Create(options));
                Assert.Contains("invalid newline or control characters", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Test DEL (127) in header value
        {
            var testValue = $"prefix{(char)127}suffix";
            var options = new GeminiClientOptions
            {
                ApiKey = "test-key",
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["X-Custom-Del"] = testValue
                }
            };

            var ex = Assert.Throws<ArgumentException>(() => GeminiRequestHeaders.Create(options));
            Assert.Contains("invalid newline or control characters", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GeminiRequestHeaders_HeaderName_ControlChars0To31And127_AllRejectedIncludingTab()
    {
        // For header names, EVERY control character from 0 to 31 INCLUDING '\t', plus 127, MUST be rejected!
        for (var c = 0; c <= 31; c++)
        {
            var ch = (char)c;
            var headerName = $"X-Bad{ch}Name";

            var options = new GeminiClientOptions
            {
                ApiKey = "test-key",
                AdditionalHeaders = new Dictionary<string, string>
                {
                    [headerName] = "valid-value"
                }
            };

            var ex = Assert.Throws<ArgumentException>(() => GeminiRequestHeaders.Create(options));
            Assert.Contains("invalid newline or control characters", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // DEL (127) in header name
        {
            var headerName = $"X-Bad{(char)127}Name";
            var options = new GeminiClientOptions
            {
                ApiKey = "test-key",
                AdditionalHeaders = new Dictionary<string, string>
                {
                    [headerName] = "valid-value"
                }
            };

            var ex = Assert.Throws<ArgumentException>(() => GeminiRequestHeaders.Create(options));
            Assert.Contains("invalid newline or control characters", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GeminiRequestHeaders_ApiKeyProvider_TabAllowed_ControlCharsAndDelRejected()
    {
        // 1. ApiKeyProvider returning key with '\t' is ALLOWED
        {
            var options = new GeminiClientOptions
            {
                ApiKeyProvider = _ => ValueTask.FromResult("Bearer\tDynamicKeyWithTab")
            };

            var headers = GeminiRequestHeaders.Create(options);
            using var message = new HttpRequestMessage();
            await headers.ApplyAsync(message, CancellationToken.None);

            Assert.True(message.Headers.TryGetValues("x-goog-api-key", out var vals));
            Assert.Equal("Bearer\tDynamicKeyWithTab", Assert.Single(vals));
        }

        // 2. ApiKeyProvider returning control char (e.g. \r, \n, NUL, DEL) throws InvalidOperationException
        char[] badChars = ['\r', '\n', '\0', (char)127, (char)1];
        foreach (var bad in badChars)
        {
            var options = new GeminiClientOptions
            {
                ApiKeyProvider = _ => ValueTask.FromResult($"KeyWith{bad}Char")
            };

            var headers = GeminiRequestHeaders.Create(options);
            using var message = new HttpRequestMessage();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await headers.ApplyAsync(message, CancellationToken.None);
            });
            Assert.Contains("control/newline-containing", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        // 3. ApiKeyProvider returning only whitespace or tab throws InvalidOperationException
        {
            var options = new GeminiClientOptions
            {
                ApiKeyProvider = _ => ValueTask.FromResult("\t  \t")
            };

            var headers = GeminiRequestHeaders.Create(options);
            using var message = new HttpRequestMessage();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await headers.ApplyAsync(message, CancellationToken.None);
            });
            Assert.Contains("empty, whitespace-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GeminiRequestHeaders_CaseInsensitiveDuplicates_ThrowsArgumentException()
    {
        var options = new GeminiClientOptions
        {
            ApiKey = "test-key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["X-Custom-Header"] = "value1",
                ["x-custom-header"] = "value2"
            }
        };

        var ex = Assert.Throws<ArgumentException>(() => GeminiRequestHeaders.Create(options));
        Assert.Contains("Duplicate header 'x-custom-header'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Content-Length")]
    [InlineData("content-type")]
    public void GeminiRequestHeaders_ContentHeaders_Rejected(string contentHeaderName)
    {
        var options = new GeminiClientOptions
        {
            ApiKey = "test-key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                [contentHeaderName] = "application/json"
            }
        };

        var ex = Assert.Throws<ArgumentException>(() => GeminiRequestHeaders.Create(options));
        Assert.Contains("not a valid request header", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("x-api-key\t")]
    [InlineData("x-api-key\r")]
    [InlineData("x-api-key\n")]
    [InlineData("x-api-key\0")]
    public void GeminiRequestHeaders_ApiKeyHeaderName_InvalidChars_ThrowsArgumentException(string headerName)
    {
        var options = new GeminiClientOptions
        {
            ApiKeyHeader = headerName,
            ApiKey = "test-key"
        };

        var ex = Assert.Throws<ArgumentException>(() => GeminiRequestHeaders.Create(options));
        Assert.Contains("invalid newline or control characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Additional Adversarial Streaming Scenarios

    [Fact]
    public async Task GeminiStreamReader_BomSplitAcrossChunks_StrippedCorrectly()
    {
        // UTF-8 BOM is 0xEF, 0xBB, 0xBF
        byte[] chunk1 = [0xEF];
        var chunk2 = new List<byte> { 0xBB, 0xBF };
        chunk2.AddRange(Encoding.UTF8.GetBytes($"data: {s_sampleJson}\n\n"));

        await using var stream = new ChunkStream(chunk1, [.. chunk2]);
        using var watchdog = new StreamIdleWatchdog(Timeout.InfiniteTimeSpan, CancellationToken.None);

        var responses = new List<GenerateContentResponse>();
        await foreach (var resp in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            responses.Add(resp);
        }

        Assert.Single(responses);
        Assert.Equal("sample text", responses[0].Text);
    }

    [Fact]
    public async Task GeminiStreamReader_JsonArray_EnforcesMaxElementBytesStrictly()
    {
        const int limit = 80;
        var element1 = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"short\"}]}}]}";
        var element2Exceeding = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + new string('z', 200) + "\"}]}}]}";
        var jsonStream = $"[{element1},\n{element2Exceeding}]";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonStream));
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

        Assert.Contains("exceeded the maximum allowed length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion


    #region Helper Stream Classes

    private sealed class ChunkStream(params byte[][] chunks) : Stream
    {
        private int _chunkIdx;
        private int _chunkOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_chunkIdx < chunks.Length)
            {
                var cur = chunks[_chunkIdx];
                var rem = cur.Length - _chunkOffset;
                if (rem <= 0)
                {
                    _chunkIdx++;
                    _chunkOffset = 0;
                    continue;
                }

                var count = Math.Min(buffer.Length, rem);
                cur.AsMemory(_chunkOffset, count).CopyTo(buffer);
                _chunkOffset += count;
                if (_chunkOffset >= cur.Length)
                {
                    _chunkIdx++;
                    _chunkOffset = 0;
                }

                return ValueTask.FromResult(count);
            }

            return ValueTask.FromResult(0);
        }
    }

    private sealed class FixedChunkSizeStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length)
            {
                return ValueTask.FromResult(0);
            }

            var toRead = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
            data.AsMemory(_position, toRead).CopyTo(buffer);
            _position += toRead;
            return ValueTask.FromResult(toRead);
        }
    }

    #endregion
}
