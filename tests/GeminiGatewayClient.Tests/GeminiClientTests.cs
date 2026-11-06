using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GeminiGatewayClient.Models;

namespace GeminiGatewayClient.Tests;

public class GeminiClientTests
{
    private const string DefaultModel = "gemini-2.5-flash";

    private const string OkResponseJson = """
        {
          "candidates": [
            {
              "index": 0,
              "content": {
                "role": "model",
                "parts": [
                  { "text": "Hello from Gemini!" }
                ]
              },
              "finishReason": "STOP"
            }
          ],
          "usageMetadata": {
            "promptTokenCount": 5,
            "candidatesTokenCount": 4,
            "totalTokenCount": 9
          },
          "modelVersion": "gemini-2.5-flash",
          "responseId": "resp-123"
        }
        """;

    [Fact]
    public async Task DefaultApiKeyHeader_IsXGoogApiKey()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "secret-key"
        }, httpClient);

        var response = await client.GenerateContentAsync("Hi");

        Assert.Equal("Hello from Gemini!", response.Text);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            handler.LastRequest.RequestUri?.ToString());
        Assert.True(handler.LastRequest.Headers.TryGetValues("x-goog-api-key", out var values));
        Assert.Equal("secret-key", Assert.Single(values));
    }

    [Fact]
    public void ConvenienceConstructor_SetsApiKeyAndDefaultModel()
    {
        using var client = new GeminiClient("my-api-key", "gemini-2.5-pro");
        Assert.Equal("gemini-2.5-pro", client.DefaultModel);
        Assert.Equal(new Uri("https://generativelanguage.googleapis.com/v1beta"), client.Endpoint);
    }

    [Fact]
    public async Task CustomApiKeyHeader_ReplacesDefault()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta"),
            Model = DefaultModel,
            ApiKey = "apim-sub-key",
            ApiKeyHeader = "Ocp-Apim-Subscription-Key"
        }, httpClient);

        await client.GenerateContentAsync("Hi");

        Assert.NotNull(handler.LastRequest);
        Assert.False(handler.LastRequest.Headers.Contains("x-goog-api-key"));
        Assert.True(handler.LastRequest.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var values));
        Assert.Equal("apim-sub-key", Assert.Single(values));
    }

    [Fact]
    public async Task ApiKeyPrefix_IsPrepended()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("https://llm-proxy.internal/gemini"),
            Model = DefaultModel,
            ApiKey = "proxy-jwt-token",
            ApiKeyHeader = "Authorization",
            ApiKeyPrefix = "Bearer "
        }, httpClient);

        await client.GenerateContentAsync("Hi");

        Assert.NotNull(handler.LastRequest);
        Assert.False(handler.LastRequest.Headers.Contains("x-goog-api-key"));
        Assert.True(handler.LastRequest.Headers.TryGetValues("Authorization", out var values));
        Assert.Equal("Bearer proxy-jwt-token", Assert.Single(values));
    }

    [Fact]
    public async Task ApiKeyProvider_IsInvokedPerRequest_AndSupportsTokenRotation()
    {
        var callCount = 0;
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKeyHeader = "Authorization",
            ApiKeyPrefix = "Bearer ",
            ApiKeyProvider = _ => ValueTask.FromResult($"dynamic-token-{++callCount}")
        }, httpClient);

        await client.GenerateContentAsync("First");
        await client.GenerateContentAsync("Second");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer dynamic-token-1", Assert.Single(handler.Requests[0].Headers.GetValues("Authorization")));
        Assert.Equal("Bearer dynamic-token-2", Assert.Single(handler.Requests[1].Headers.GetValues("Authorization")));
    }

    [Fact]
    public async Task AdditionalHeaders_AreSentAlongsideApiKey()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("https://gateway.ai.cloudflare.com/v1/acc/gw/google-ai-studio/v1beta"),
            Model = DefaultModel,
            ApiKey = "gemini-key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["cf-aig-authorization"] = "Bearer cf-token",
                ["x-trace-id"] = "trace-123"
            }
        }, httpClient);

        await client.GenerateContentAsync("Hi");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("gemini-key", Assert.Single(handler.LastRequest.Headers.GetValues("x-goog-api-key")));
        Assert.Equal("Bearer cf-token", Assert.Single(handler.LastRequest.Headers.GetValues("cf-aig-authorization")));
        Assert.Equal("trace-123", Assert.Single(handler.LastRequest.Headers.GetValues("x-trace-id")));
    }

    [Fact]
    public async Task NullApiKey_SendsNoKeyHeader()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = null,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["cf-aig-authorization"] = "Bearer gateway-only"
            }
        }, httpClient);

        await client.GenerateContentAsync("Hi");

        Assert.NotNull(handler.LastRequest);
        Assert.False(handler.LastRequest.Headers.Contains("x-goog-api-key"));
        Assert.Equal("Bearer gateway-only", Assert.Single(handler.LastRequest.Headers.GetValues("cf-aig-authorization")));
    }

    [Theory]
    [InlineData("https://example.com/v1beta", "gemini-2.5-flash", null, "https://example.com/v1beta/models/gemini-2.5-flash:generateContent")]
    [InlineData("https://example.com/v1beta/", "models/gemini-2.5-flash", null, "https://example.com/v1beta/models/gemini-2.5-flash:generateContent")]
    [InlineData("https://example.com/v1beta", "tunedModels/my-custom-model", null, "https://example.com/v1beta/tunedModels/my-custom-model:generateContent")]
    [InlineData("https://example.com/v1beta", "gemini-2.5-flash", "gemini-2.5-pro", "https://example.com/v1beta/models/gemini-2.5-pro:generateContent")]
    [InlineData("https://example.com/v1beta", null, "tunedModels/override-1", "https://example.com/v1beta/tunedModels/override-1:generateContent")]
    public async Task Endpoint_IsComposedWithModelAndOperation(
        string endpoint,
        string? defaultModel,
        string? perCallModel,
        string expectedUri)
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri(endpoint),
            Model = defaultModel,
            ApiKey = "key"
        }, httpClient);

        await client.GenerateContentAsync("Hi", model: perCallModel);

        Assert.Equal(expectedUri, handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task GenerateContentAsync_WithoutDefaultOrPerCallModel_ThrowsInvalidOperationException()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            ApiKey = "key"
        }, httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateContentAsync("Hi"));
    }

    [Fact]
    public async Task Stream_DefaultSseFormat_AppendsAltSseAndParsesEvents()
    {
        const string ssePayload = """
            : keep-alive comment

            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Hello "}]}}]}

            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"SSE!"}]},"finishReason":"STOP"}]}

            data: [DONE]

            """;

        using var handler = new RecordingHandler(ssePayload, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var chunks = new List<string?>();
        await foreach (var chunk in client.StreamGenerateContentAsync("Hi"))
        {
            chunks.Add(chunk.Text);
        }

        Assert.Equal(["Hello ", "SSE!"], chunks);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.Contains(handler.LastRequest!.Headers.Accept, h => h.MediaType == "text/event-stream");
    }

    [Fact]
    public async Task Stream_StalledResponse_ThrowsTimeoutExceptionAfterStreamIdleTimeout()
    {
        using var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StallingStreamContent(
                    "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"First\"}]}}]}\n\n")
            };
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key",
            StreamIdleTimeout = TimeSpan.FromMilliseconds(150)
        }, httpClient);

        var received = new List<string?>();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var chunk in client.StreamGenerateContentAsync("Hi", cancellationToken: guard.Token))
            {
                received.Add(chunk.Text);
            }
        });

        Assert.Equal(["First"], received);
    }

    [Fact]
    public async Task Stream_SlowConsumerDuringYieldReturn_DoesNotTriggerStreamIdleTimeout()
    {
        const string ssePayload = """
            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Chunk 1"}]}}]}

            data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Chunk 2"}]},"finishReason":"STOP"}]}

            """;

        using var handler = new RecordingHandler(ssePayload, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key",
            StreamIdleTimeout = TimeSpan.FromMilliseconds(80)
        }, httpClient);

        var received = new List<string?>();
        await foreach (var chunk in client.StreamGenerateContentAsync("Hi"))
        {
            received.Add(chunk.Text);
            // Consumer takes longer than StreamIdleTimeout to process the yielded item
            await Task.Delay(200);
        }

        Assert.Equal(["Chunk 1", "Chunk 2"], received);
    }

    [Theory]
    [InlineData("-5s")]
    [InlineData("999999999999999999999s")]
    [InlineData("NaNs")]
    [InlineData("Infinitys")]
    public async Task ErrorStatus_InvalidOrOverflowRetryDelay_DoesNotThrowOverflowException(string badRetryDelay)
    {
        var errorBody = $$"""
            {
              "error": {
                "code": 429,
                "message": "Rate limited",
                "status": "RESOURCE_EXHAUSTED",
                "details": [
                  {
                    "@type": "type.googleapis.com/google.rpc.RetryInfo",
                    "retryDelay": "{{badRetryDelay}}"
                  }
                ]
              }
            }
            """;

        using var handler = new RecordingHandler(errorBody, HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync("Hi"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task ErrorStatus_ParsesGoogleErrorStatusMessageAndRetryInfo()
    {
        const string errorBody = """
            {
              "error": {
                "code": 429,
                "message": "Resource has been exhausted (e.g. check quota).",
                "status": "RESOURCE_EXHAUSTED",
                "details": [
                  {
                    "@type": "type.googleapis.com/google.rpc.RetryInfo",
                    "retryDelay": "37s"
                  }
                ]
              }
            }
            """;

        using var handler = new RecordingHandler(errorBody, HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync("Hi"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal("RESOURCE_EXHAUSTED", ex.ErrorStatus);
        Assert.Equal("Resource has been exhausted (e.g. check quota).", ex.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(37), ex.RetryAfter);
        Assert.Contains("RESOURCE_EXHAUSTED", ex.Message);
        Assert.Contains("Resource has been exhausted", ex.Message);
    }

    [Fact]
    public async Task Stream_HttpError_ThrowsGeminiApiException()
    {
        const string errorBody = """{"error":{"code":401,"message":"Invalid credentials","status":"UNAUTHENTICATED"}}""";
        using var handler = new RecordingHandler(errorBody, HttpStatusCode.Unauthorized);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "bad-key"
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in client.StreamGenerateContentAsync("Hi"))
            {
            }
        });

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("UNAUTHENTICATED", ex.ErrorStatus);
    }

    [Fact]
    public async Task CancellationToken_IsPropagatedToHttpClient()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GenerateContentAsync("Hi", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ExternalHttpClient_IsNotDisposed_AndDefaultHeadersAreUntouched()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);

        var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        await client.GenerateContentAsync("First call");
        client.Dispose();

        Assert.Empty(httpClient.DefaultRequestHeaders);

        using var secondClient = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var response = await secondClient.GenerateContentAsync("Second call");
        Assert.Equal("Hello from Gemini!", response.Text);
    }

    [Fact]
    public async Task GenerateContentAsync_MalformedJsonOnHttp200_IncludesRawBodyInErrorContent()
    {
        const string gatewayHtml = "<html><body>502 Bad Gateway</body></html>";
        using var handler = new RecordingHandler(gatewayHtml, HttpStatusCode.OK, contentType: "text/html");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync("Hi"));
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
        Assert.Equal(gatewayHtml, ex.ErrorContent);
    }

    [Fact]
    public async Task Endpoint_ModelWithSpacesOrSpecialChars_PercentEncodesOnceWithoutDoubleEncoding()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("https://example.com/v1beta"),
            Model = "tunedModels/my custom model",
            ApiKey = "key"
        }, httpClient);

        await client.GenerateContentAsync("Hi");

        Assert.Equal(
            "https://example.com/v1beta/tunedModels/my%20custom%20model:generateContent",
            handler.LastRequest?.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Stream_SseWithSplitCrLfAndMultiByteUtf8AcrossByteReads_ParsesCleanly()
    {
        const string sse = "\uFEFFdata: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"こんにちは🌸\"}]}}]}\r\n\r\ndata: [DONE]\r\n\r\n";
        var rawBytes = Encoding.UTF8.GetBytes(sse);

        using var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteByByteStreamContent(rawBytes)
            };
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var chunks = new List<string?>();
        await foreach (var chunk in client.StreamGenerateContentAsync("Hi"))
        {
            chunks.Add(chunk.Text);
        }

        Assert.Equal(["こんにちは🌸"], chunks);
    }

    [Fact]
    public async Task Stream_InStreamErrorWithGrpcUnknownCode_MapsToInternalServerErrorInsteadOfHttp200()
    {
        const string sseError = """
            data: {"error":{"code":2,"message":"Stream broken upstream","status":"UNKNOWN"}}

            """;

        using var handler = new RecordingHandler(sseError, HttpStatusCode.OK, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in client.StreamGenerateContentAsync("Hi"))
            {
            }
        });

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal("UNKNOWN", ex.ErrorStatus);
        Assert.Equal("Stream broken upstream", ex.ErrorMessage);
    }

    [Fact]
    public async Task DisposedClient_ThrowsObjectDisposedExceptionEagerly()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GenerateContentAsync("Hi"));
        Assert.Throws<ObjectDisposedException>(() => { _ = client.StreamGenerateContentAsync("Hi"); });
    }

    [Fact]
    public async Task Stream_SseFormat_BareDataFieldWithoutColon_AppendsBlankLinePerWhatwgSpec()
    {
        const string sse = "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Ok\"}]}}]}\n" +
                           "data\n\n";
        using var handler = new RecordingHandler(sse, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var chunks = new List<string?>();
        await foreach (var chunk in client.StreamGenerateContentAsync("Hi"))
        {
            chunks.Add(chunk.Text);
        }

        Assert.Equal(["Ok"], chunks);
    }

    [Fact]
    public async Task ErrorResponse_PopulatesErrorCodeProperty()
    {
        const string errorJson = """
            {
              "error": {
                "code": 429,
                "message": "Resource has been exhausted",
                "status": "RESOURCE_EXHAUSTED"
              }
            }
            """;

        using var handler = new RecordingHandler(errorJson, HttpStatusCode.TooManyRequests);
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync("Hi"));
        Assert.Equal(429, ex.ErrorCode);
        Assert.Equal("RESOURCE_EXHAUSTED", ex.ErrorStatus);
    }

    [Fact]
    public async Task Endpoint_ProxyWithColonInIntermediatePathSegment_IsAllowed_AndDoesNotDuplicateAltSse()
    {
        const string ssePayload = "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]}}]}\n\n";
        using var handler = new RecordingHandler(ssePayload, contentType: "text/event-stream");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = new Uri("https://gateway.internal/env:prod/v1beta?alt=sse"),
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        await foreach (var _ in client.StreamGenerateContentAsync("Hi"))
        {
        }

        Assert.Equal(
            "https://gateway.internal/env:prod/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse",
            handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public void TryGetModelContent_ReturnsFalseWhenCandidateContentHasEmptyPartsOnMaxTokens_AndTrueWhenNonEmpty()
    {
        var maxTokensEmptyPartsResponse = JsonSerializer.Deserialize<GenerateContentResponse>(
            """
            {
              "candidates": [
                {
                  "content": { "role": "model" },
                  "finishReason": "MAX_TOKENS",
                  "index": 0
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 14,
                "thoughtsTokenCount": 19,
                "totalTokenCount": 33
              }
            }
            """,
            GeminiGatewayClient.Serialization.GeminiJsonContext.Default.GenerateContentResponse)!;

        Assert.False(maxTokensEmptyPartsResponse.TryGetModelContent(out var emptyContent));
        Assert.Null(emptyContent);

        var pureEmptyTextPartResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [new Part { Text = string.Empty }]
                    }
                }
            ]
        };

        Assert.False(pureEmptyTextPartResponse.TryGetModelContent(out _));

        var validResponse = new GenerateContentResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts = [Part.FromText("Valid output")]
                    }
                }
            ]
        };

        Assert.True(validResponse.TryGetModelContent(out var modelContent));
        Assert.NotNull(modelContent);
        Assert.Single(modelContent.Parts);
        Assert.Equal("Valid output", modelContent.Parts[0].Text);
    }

    [Fact]
    public async Task Stream_LargeSingleSseLineAcrossManySmallReads_ParsesCleanlyWithIncrementalSearch()
    {
        var largeText = new string('A', 12_000);
        var sse = $"data: {{\"candidates\":[{{\"index\":0,\"content\":{{\"role\":\"model\",\"parts\":[{{\"text\":\"{largeText}\"}}]}}}}]}}\r\n\r\n" +
                  "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"_tail\"}]},\"finishReason\":\"STOP\"}]}\r\n\r\n";
        var rawBytes = Encoding.UTF8.GetBytes(sse);

        // Split into 128-byte chunks to force dozens of incremental ReadAsync calls and buffer growth within a single SSE line
        var chunks = rawBytes.Chunk(128).ToArray();

        using var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DelayedMultiChunkStreamContent(chunks, TimeSpan.Zero)
            };
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = DefaultModel,
            ApiKey = "key"
        }, httpClient);

        var received = new StringBuilder();
        await foreach (var chunk in client.StreamGenerateContentAsync("Hi"))
        {
            received.Append(chunk.Text);
        }

        Assert.Equal(largeText + "_tail", received.ToString());
    }

    private sealed class DelayedMultiChunkStreamContent(byte[][] chunks, TimeSpan delayPerRead) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new DelayedMultiChunkStream(chunks, delayPerRead));
    }

    private sealed class DelayedMultiChunkStream(byte[][] chunks, TimeSpan delayPerRead) : Stream
    {
        private int _chunkIndex;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_chunkIndex < chunks.Length && _offset >= chunks[_chunkIndex].Length)
            {
                _chunkIndex++;
                _offset = 0;
            }

            if (_chunkIndex >= chunks.Length)
            {
                return 0;
            }

            // Intentionally do not pass cancellationToken to Delay so we simulate bytes arriving in the race window
            // right after the watchdog timer fires.
            await Task.Delay(delayPerRead, CancellationToken.None);

            var current = chunks[_chunkIndex];
            var count = Math.Min(buffer.Length, current.Length - _offset);
            current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ByteByByteStreamContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new OneByteAtATimeStream(bytes));
    }

    private sealed class OneByteAtATimeStream(byte[] bytes) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= bytes.Length || buffer.IsEmpty)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = bytes[_offset++];
            return ValueTask.FromResult(1);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StallingStreamContent(string initialChunk) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new StallingReadStream(Encoding.UTF8.GetBytes(initialChunk)));
    }

    private sealed class StallingReadStream(byte[] initialBytes) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < initialBytes.Length)
            {
                var count = Math.Min(buffer.Length, initialBytes.Length - _offset);
                initialBytes.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
