using System.Net;
using System.Text;
using GeminiGatewayClient;
using GeminiGatewayClient.Http;
using GeminiGatewayClient.Models;
using Xunit;

namespace GeminiGatewayClient.Tests;

public sealed class GatewayHeaderValidationTests
{
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
                Assert.Contains("printable ASCII", ex.Message, StringComparison.OrdinalIgnoreCase);
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
            Assert.Contains("printable ASCII", ex.Message, StringComparison.OrdinalIgnoreCase);
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
            Assert.Contains("printable ASCII", ex.Message, StringComparison.OrdinalIgnoreCase);
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
            Assert.Contains("printable ASCII", ex.Message, StringComparison.OrdinalIgnoreCase);
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
            Assert.Contains("non-ASCII", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("printable ASCII", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
