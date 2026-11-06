using System.Text;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Checks against the real API that streaming survives a gateway that does not forward <c>alt=sse</c>.
/// </summary>
public sealed class StreamFormatExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    [ExploratoryFact]
    public async Task Stream_WithoutAltSse_ReadsTheJsonArrayGeminiSends()
    {
        LogSection("Streaming without alt=sse");
        var options = new GeminiClientOptions { ApiKey = ExploratoryTestSettings.GetRequiredApiKey(), Model = ExploratoryTestSettings.GetModel() };
        using var handler = new QueryDroppingHandler();
        using var client = new GeminiClient(options, new HttpClient(handler));
        var text = new StringBuilder();
        var chunks = 0;

        await foreach (var chunk in client.StreamGenerateContentAsync("Count from 1 to 20, one number per line."))
        {
            chunks++;
            text.Append(chunk.Text);
        }

        Output.WriteLine($"chunks={chunks}, media type={handler.LastMediaType}");
        Assert.StartsWith("application/json", handler.LastMediaType, StringComparison.Ordinal);
        Assert.Contains("20", text.ToString(), StringComparison.Ordinal);
    }

    [ExploratoryFact]
    public async Task Stream_WithServerSentEventsOff_ReadsTheJsonArrayGeminiSends()
    {
        LogSection("Streaming with UseServerSentEvents = false");
        using var client = CreateGeminiClient(options => options.UseServerSentEvents = false);
        var text = new StringBuilder();

        await foreach (var chunk in client.StreamGenerateContentAsync("Count from 1 to 20, one number per line."))
        {
            text.Append(chunk.Text);
        }

        Assert.Contains("20", text.ToString(), StringComparison.Ordinal);
    }

    private sealed class QueryDroppingHandler() : DelegatingHandler(GeminiClient.CreateDefaultHandler())
    {
        public string? LastMediaType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Query = string.Empty }.Uri;
            var response = await base.SendAsync(request, cancellationToken);
            LastMediaType = response.Content.Headers.ContentType?.ToString();
            return response;
        }
    }
}
