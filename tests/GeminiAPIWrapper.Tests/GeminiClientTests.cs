using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GeminiAPIWrapper.ApiException;
using GeminiAPIWrapper.Configurations;
using GeminiAPIWrapper.Extensions;
using GeminiAPIWrapper.Options;

namespace GeminiAPIWrapper.Tests;

public class GeminiClientTests
{
    private const string SampleEndpoint = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash";

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
    public async Task DefaultKeyHeader_IsXGoogApiKey()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "secret-key"
        }, httpClient);

        var response = await service.GenerateAsync("Hi");

        Assert.Equal("Hello from Gemini!", response.GetText());
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("x-goog-api-key", out var values));
        Assert.Equal("secret-key", Assert.Single(values));
    }

    [Fact]
    public async Task CustomKeyHeader_ReplacesDefault()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = "https://contoso.azure-api.net/gemini/v1beta/models/gemini-2.5-flash",
            ApiKey = "apim-sub-key",
            KeyHeader = "Ocp-Apim-Subscription-Key"
        }, httpClient);

        await service.GenerateAsync("Hi");

        Assert.NotNull(handler.LastRequest);
        Assert.False(handler.LastRequest.Headers.Contains("x-goog-api-key"));
        Assert.True(handler.LastRequest.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var values));
        Assert.Equal("apim-sub-key", Assert.Single(values));
    }

    [Fact]
    public async Task KeyPrefix_IsPrepended()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = "https://llm-proxy.internal/gemini/models/gemini-2.5-flash",
            ApiKey = "proxy-jwt-token",
            KeyHeader = "Authorization",
            KeyPrefix = "Bearer "
        }, httpClient);

        await service.GenerateAsync("Hi");

        Assert.NotNull(handler.LastRequest);
        Assert.False(handler.LastRequest.Headers.Contains("x-goog-api-key"));
        Assert.True(handler.LastRequest.Headers.TryGetValues("Authorization", out var values));
        Assert.Equal("Bearer proxy-jwt-token", Assert.Single(values));
    }

    [Fact]
    public async Task AdditionalHeaders_AreSentAlongsideApiKey()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = "https://gateway.ai.cloudflare.com/v1/acc/gw/google-ai-studio/v1beta/models/gemini-2.5-flash",
            ApiKey = "gemini-key",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["cf-aig-authorization"] = "Bearer cf-token",
                ["x-trace-id"] = "trace-123"
            }
        }, httpClient);

        await service.GenerateAsync("Hi");

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
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = null,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["cf-aig-authorization"] = "Bearer gateway-only"
            }
        }, httpClient);

        await service.GenerateAsync("Hi");

        Assert.NotNull(handler.LastRequest);
        Assert.False(handler.LastRequest.Headers.Contains("x-goog-api-key"));
        Assert.Equal("Bearer gateway-only", Assert.Single(handler.LastRequest.Headers.GetValues("cf-aig-authorization")));
    }

    [Theory]
    [InlineData("https://example.com/v1beta/models/gemini-2.5-flash")]
    [InlineData("https://example.com/v1beta/models/gemini-2.5-flash/")]
    [InlineData("https://example.com/v1beta/models/gemini-2.5-flash:")]
    public async Task Endpoint_IsComposedWithOperation_WithoutDuplicateSeparators(string endpointBase)
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = endpointBase,
            ApiKey = "key"
        }, httpClient);

        await service.GenerateAsync("Hi");

        Assert.Equal(
            "https://example.com/v1beta/models/gemini-2.5-flash:generateContent",
            handler.LastRequest?.RequestUri?.ToString());
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
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key"
        }, httpClient);

        var chunks = new List<string?>();
        await foreach (var chunk in service.StreamGenerateAsync("Hi"))
        {
            chunks.Add(chunk.GetText());
        }

        Assert.Equal(["Hello ", "SSE!"], chunks);
        Assert.Equal(
            $"{SampleEndpoint}:streamGenerateContent?alt=sse",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.Contains(handler.LastRequest!.Headers.Accept, h => h.MediaType == "text/event-stream");
    }

    [Fact]
    public async Task Stream_JsonArrayFormat_ParsesJsonArrayChunksWithoutAltSse()
    {
        const string streamJson = """
            [
              {
                "candidates": [
                  {
                    "index": 0,
                    "content": { "role": "model", "parts": [ { "text": "Hello " } ] }
                  }
                ]
              },
              {
                "candidates": [
                  {
                    "index": 0,
                    "content": { "role": "model", "parts": [ { "text": "world!" } ] },
                    "finishReason": "STOP"
                  }
                ]
              }
            ]
            """;

        using var handler = new RecordingHandler(streamJson);
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key",
            StreamingFormat = GeminiStreamingFormat.JsonArray
        }, httpClient);

        var chunks = new List<string?>();
        await foreach (var chunk in service.StreamGenerateAsync("Hi"))
        {
            chunks.Add(chunk.GetText());
        }

        Assert.Equal(["Hello ", "world!"], chunks);
        Assert.Equal(
            $"{SampleEndpoint}:streamGenerateContent",
            handler.LastRequest?.RequestUri?.ToString());
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
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key"
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => service.GenerateAsync("Hi"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal("RESOURCE_EXHAUSTED", ex.ErrorStatus);
        Assert.Equal("Resource has been exhausted (e.g. check quota).", ex.ErrorMessage);
        Assert.Equal(TimeSpan.FromSeconds(37), ex.RetryAfter);
        Assert.Contains("RESOURCE_EXHAUSTED", ex.Message);
        Assert.Contains("Resource has been exhausted", ex.Message);
    }

    [Fact]
    public async Task ErrorStatus_FallsBackToHttpRetryAfterHeader_AndTruncatesOversizedBody()
    {
        var largeBody = new string('X', 80 * 1024);
        using var handler = new RecordingHandler(
            largeBody,
            HttpStatusCode.ServiceUnavailable,
            configureResponse: resp => resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12)));
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key"
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => service.GenerateAsync("Hi"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(12), ex.RetryAfter);
        Assert.NotNull(ex.ErrorContent);
        Assert.Equal(64 * 1024, ex.ErrorContent.Length);
    }

    [Theory]
    [InlineData(GeminiStreamingFormat.ServerSentEvents, "data: {not-valid-json}\n\n")]
    [InlineData(GeminiStreamingFormat.JsonArray, "[{not-valid-json}]")]
    public async Task Stream_MalformedJson_ThrowsGeminiApiExceptionWithInnerJsonException(
        GeminiStreamingFormat format,
        string badPayload)
    {
        using var handler = new RecordingHandler(badPayload);
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key",
            StreamingFormat = format
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in service.StreamGenerateAsync("Hi"))
            {
            }
        });

        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task Stream_HttpError_ThrowsGeminiApiException()
    {
        const string errorBody = """{"error":{"code":401,"message":"Invalid credentials","status":"UNAUTHENTICATED"}}""";
        using var handler = new RecordingHandler(errorBody, HttpStatusCode.Unauthorized);
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "bad-key"
        }, httpClient);

        var ex = await Assert.ThrowsAsync<GeminiApiException>(async () =>
        {
            await foreach (var _ in service.StreamGenerateAsync("Hi"))
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
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key"
        }, httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GenerateAsync("Hi", ct: cts.Token));
    }

    [Fact]
    public async Task ExternalHttpClient_IsNotDisposed_AndDefaultHeadersAreUntouched()
    {
        using var handler = new RecordingHandler(OkResponseJson);
        using var httpClient = new HttpClient(handler);

        var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key"
        }, httpClient);

        await service.GenerateAsync("First call");
        service.Dispose();

        Assert.Empty(httpClient.DefaultRequestHeaders);

        using var secondService = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = SampleEndpoint,
            ApiKey = "key"
        }, httpClient);

        var response = await secondService.GenerateAsync("Second call");
        Assert.Equal("Hello from Gemini!", response.GetText());
    }

    internal sealed class RecordingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string contentType = "application/json",
        Action<HttpResponseMessage>? configureResponse = null) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, contentType)
            };
            configureResponse?.Invoke(response);
            return response;
        }
    }
}
